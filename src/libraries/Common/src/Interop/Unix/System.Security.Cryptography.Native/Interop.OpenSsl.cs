// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class OpenSsl
    {
        private const string DisableTlsResumeCtxSwitch = "System.Net.Security.DisableTlsResume";
        private const string DisableTlsResumeEnvironmentVariable = "DOTNET_SYSTEM_NET_SECURITY_DISABLETLSRESUME";
        private static readonly IdnMapping s_idnMapping = new IdnMapping();

        #region internal methods
        internal static SafeChannelBindingHandle? QueryChannelBinding(SafeSslHandle context, ChannelBindingKind bindingType)
        {
            Debug.Assert(
                bindingType != ChannelBindingKind.Endpoint,
                "Endpoint binding should be handled by EndpointChannelBindingToken");

            SafeChannelBindingHandle? bindingHandle;
            switch (bindingType)
            {
                case ChannelBindingKind.Unique:
                    bindingHandle = new SafeChannelBindingHandle(bindingType);
                    QueryUniqueChannelBinding(context, bindingHandle);
                    break;

                default:
                    // Keeping parity with windows, we should return null in this case.
                    bindingHandle = null;
                    break;
            }

            return bindingHandle;
        }

         private static volatile int s_disableTlsResume = -1;

         private static bool DisableTlsResume
         {
             get
             {
                 int disableTlsResume = s_disableTlsResume;
                 if (disableTlsResume != -1)
                 {
                     return disableTlsResume != 0;
                 }

                 // First check for the AppContext switch, giving it priority over the environment variable.
                 if (AppContext.TryGetSwitch(DisableTlsResumeCtxSwitch, out bool value))
                 {
                     s_disableTlsResume = value ? 1 : 0;
                 }
                 else
                 {
                     // AppContext switch wasn't used. Check the environment variable.
                    s_disableTlsResume =
                        Environment.GetEnvironmentVariable(DisableTlsResumeEnvironmentVariable) is string envVar &&
                        (envVar == "1" || envVar.Equals("true", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                 }

                 return s_disableTlsResume != 0;
            }
        }

        // This is helper function to adjust requested protocols based on CipherSuitePolicy and system capability.
        private static SslProtocols CalculateEffectiveProtocols(SslAuthenticationOptions sslAuthenticationOptions)
        {
            SslProtocols protocols = sslAuthenticationOptions.EnabledSslProtocols;

            if (!Interop.Ssl.Capabilities.Tls13Supported)
            {
                if (protocols != SslProtocols.None &&
                    CipherSuitesPolicyPal.WantsTls13(protocols))
                {
                    protocols = protocols & (~SslProtocols.Tls13);
                }
            }
            else if (CipherSuitesPolicyPal.WantsTls13(protocols) &&
                CipherSuitesPolicyPal.ShouldOptOutOfTls13(sslAuthenticationOptions.CipherSuitesPolicy, sslAuthenticationOptions.EncryptionPolicy))
            {
                if (protocols == SslProtocols.None)
                {
                    // we are using default settings but cipher suites policy says that TLS 1.3
                    // is not compatible with our settings (i.e. we requested no encryption or disabled
                    // all TLS 1.3 cipher suites)
                    protocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;
                }
                else
                {
                    // user explicitly asks for TLS 1.3 but their policy is not compatible with TLS 1.3
                    throw new SslException(
                        SR.Format(SR.net_ssl_encryptionpolicy_notsupported, sslAuthenticationOptions.EncryptionPolicy));
                }
            }

            if (CipherSuitesPolicyPal.ShouldOptOutOfLowerThanTls13(sslAuthenticationOptions.CipherSuitesPolicy, sslAuthenticationOptions.EncryptionPolicy))
            {
                if (!CipherSuitesPolicyPal.WantsTls13(protocols))
                {
                    // We cannot provide neither TLS 1.3 or non TLS 1.3, user disabled all cipher suites
                    throw new SslException(
                        SR.Format(SR.net_ssl_encryptionpolicy_notsupported, sslAuthenticationOptions.EncryptionPolicy));
                }

                protocols = SslProtocols.Tls13;
            }

            return protocols;
        }

        // This essentially wraps SSL_CTX* aka SSL_CTX_new + setting
        internal static SafeSslContextHandle AllocateSslContext(SafeFreeSslCredentials credential, SslAuthenticationOptions sslAuthenticationOptions, SslProtocols protocols)
        {
            SafeX509Handle? certHandle = credential.CertHandle;
            SafeEvpPKeyHandle? certKeyHandle = credential.CertKeyHandle;

            // Always use SSLv23_method, regardless of protocols.  It supports negotiating to the highest
            // mutually supported version and can thus handle any of the set protocols, and we then use
            // SetProtocolOptions to ensure we only allow the ones requested.
            SafeSslContextHandle sslCtx = Ssl.SslCtxCreate(Ssl.SslMethods.SSLv23_method);
            try
            {
                if (sslCtx.IsInvalid)
                {
                    throw CreateSslException(SR.net_allocate_ssl_context_failed);
                }

                Ssl.SslCtxSetProtocolOptions(sslCtx, protocols);

                if (sslAuthenticationOptions.EncryptionPolicy != EncryptionPolicy.RequireEncryption)
                {
                    // Sets policy and security level
                    if (!Ssl.SetEncryptionPolicy(sslCtx, sslAuthenticationOptions.EncryptionPolicy))
                    {
                        throw new SslException( SR.Format(SR.net_ssl_encryptionpolicy_notsupported, sslAuthenticationOptions.EncryptionPolicy));
                    }
                }

                byte[]? cipherList =
                    CipherSuitesPolicyPal.GetOpenSslCipherList(sslAuthenticationOptions.CipherSuitesPolicy, protocols, sslAuthenticationOptions.EncryptionPolicy);

                Debug.Assert(cipherList == null || (cipherList.Length >= 1 && cipherList[cipherList.Length - 1] == 0));

                byte[]? cipherSuites =
                    CipherSuitesPolicyPal.GetOpenSslCipherSuites(sslAuthenticationOptions.CipherSuitesPolicy, protocols, sslAuthenticationOptions.EncryptionPolicy);

                Debug.Assert(cipherSuites == null || (cipherSuites.Length >= 1 && cipherSuites[cipherSuites.Length - 1] == 0));

                unsafe
                {
                    fixed (byte* cipherListStr = cipherList)
                    fixed (byte* cipherSuitesStr = cipherSuites)
                    {
                        if (!Ssl.SslCtxSetCiphers(sslCtx, cipherListStr, cipherSuitesStr))
                        {
                            Crypto.ErrClearError();
                            throw new PlatformNotSupportedException(SR.Format(SR.net_ssl_encryptionpolicy_notsupported, sslAuthenticationOptions.EncryptionPolicy));
                        }
                    }
                }

                // The logic in SafeSslHandle.Disconnect is simple because we are doing a quiet
                // shutdown (we aren't negotiating for session close to enable later session
                // restoration).
                //
                // If you find yourself wanting to remove this line to enable bidirectional
                // close-notify, you'll probably need to rewrite SafeSslHandle.Disconnect().
                // https://www.openssl.org/docs/manmaster/ssl/SSL_shutdown.html
                Ssl.SslCtxSetQuietShutdown(sslCtx);

                if (sslAuthenticationOptions.IsServer && sslAuthenticationOptions.ApplicationProtocols != null && sslAuthenticationOptions.ApplicationProtocols.Count != 0)
                {
                    unsafe
                    {
                        Interop.Ssl.SslCtxSetAlpnSelectCb(sslCtx, &AlpnServerSelectCallback, IntPtr.Zero);
                    }
                }

                bool hasCertificateAndKey =
                    certHandle != null && !certHandle.IsInvalid
                    && certKeyHandle != null && !certKeyHandle.IsInvalid;

                if (hasCertificateAndKey)
                {
                    SetSslCertificate(sslCtx, certHandle!, certKeyHandle!);
                }

                if (sslAuthenticationOptions.CertificateContext != null && sslAuthenticationOptions.CertificateContext.IntermediateCertificates.Length > 0)
                {
                    if (!Ssl.AddExtraChainCertificates(sslCtx, sslAuthenticationOptions.CertificateContext.IntermediateCertificates))
                    {
                        throw CreateSslException(SR.net_ssl_use_cert_failed);
                    }
                }
            }
            catch
            {
                sslCtx.Dispose();
                throw;
            }

            return sslCtx;
        }

        // This essentially wraps SSL* SSL_new()
        internal static SafeSslHandle AllocateSslHandle(SafeFreeSslCredentials credential, SslAuthenticationOptions sslAuthenticationOptions)
        {
            SafeSslHandle? sslHandle = null;
            SafeSslContextHandle? sslCtxHandle = null;
            SafeSslContextHandle? newCtxHandle = null;
            SslProtocols protocols = CalculateEffectiveProtocols(sslAuthenticationOptions);
            bool cacheSslContext = !DisableTlsResume && sslAuthenticationOptions.EncryptionPolicy == EncryptionPolicy.RequireEncryption &&
                    sslAuthenticationOptions.CertificateContext != null &&
                    sslAuthenticationOptions.CertificateContext.SslContexts != null &&
                    sslAuthenticationOptions.CipherSuitesPolicy == null &&
                    (!sslAuthenticationOptions.IsServer ||
                    (sslAuthenticationOptions.ApplicationProtocols != null && sslAuthenticationOptions.ApplicationProtocols.Count != 0));

            if (cacheSslContext)
            {
               sslAuthenticationOptions.CertificateContext!.SslContexts!.TryGetValue(protocols, out sslCtxHandle);
            }

            if (sslCtxHandle == null)
            {
                // We did not get SslContext from cache
                sslCtxHandle = newCtxHandle = AllocateSslContext(credential, sslAuthenticationOptions, protocols);

                if (cacheSslContext && sslAuthenticationOptions.CertificateContext!.SslContexts!.TryAdd(protocols, newCtxHandle))
                {
                    newCtxHandle = null;
                }
            }

            GCHandle alpnHandle = default;
            try
            {
                sslHandle = SafeSslHandle.Create(sslCtxHandle, sslAuthenticationOptions.IsServer);
                Debug.Assert(sslHandle != null, "Expected non-null return value from SafeSslHandle.Create");
                if (sslHandle.IsInvalid)
                {
                    sslHandle.Dispose();
                    throw CreateSslException(SR.net_allocate_ssl_context_failed);
                }

                if (sslAuthenticationOptions.ApplicationProtocols != null && sslAuthenticationOptions.ApplicationProtocols.Count != 0)
                {
                    if (sslAuthenticationOptions.IsServer)
                    {
                        alpnHandle = GCHandle.Alloc(sslAuthenticationOptions.ApplicationProtocols);
                        Interop.Ssl.SslSetData(sslHandle, GCHandle.ToIntPtr(alpnHandle));
                        sslHandle.AlpnHandle = alpnHandle;
                    }
                    else
                    {
                        if (Interop.Ssl.SslSetAlpnProtos(sslHandle, sslAuthenticationOptions.ApplicationProtocols) != 0)
                        {
                            throw CreateSslException(SR.net_alpn_config_failed);
                        }
                    }
                }

                if (!sslAuthenticationOptions.IsServer)
                {
                    // The IdnMapping converts unicode input into the IDNA punycode sequence.
                    string punyCode = string.IsNullOrEmpty(sslAuthenticationOptions.TargetHost) ? string.Empty : s_idnMapping.GetAscii(sslAuthenticationOptions.TargetHost!);

                    // Similar to windows behavior, set SNI on openssl by default for client context, ignore errors.
                    if (!Ssl.SslSetTlsExtHostName(sslHandle, punyCode))
                    {
                        Crypto.ErrClearError();
                    }
                }

                if (sslAuthenticationOptions.IsServer && sslAuthenticationOptions.RemoteCertRequired)
                {
                    Ssl.SslSetVerifyPeer(sslHandle);
                }
            }
            catch
            {
                if (alpnHandle.IsAllocated)
                {
                    alpnHandle.Free();
                }

                throw;
            }
            finally
            {
                newCtxHandle?.Dispose();
            }

            return sslHandle;
        }

        internal static SecurityStatusPal SslRenegotiate(SafeSslHandle sslContext, out byte[]? outputBuffer)
        {
            int ret = Interop.Ssl.SslRenegotiate(sslContext);

            outputBuffer = Array.Empty<byte>();
            if (ret != 1)
            {
                GetSslError(sslContext, ret, out Exception? exception);
                return new SecurityStatusPal(SecurityStatusPalErrorCode.InternalError, exception);
            }
            return new SecurityStatusPal(SecurityStatusPalErrorCode.OK);
        }

        internal static bool DoSslHandshake(SafeSslHandle context, ReadOnlySpan<byte> input, out byte[]? sendBuf, out int sendCount)
        {
            sendBuf = null;
            sendCount = 0;
            Exception? handshakeException = null;

            if (input.Length > 0)
            {
                if (Ssl.BioWrite(context.InputBio!, ref MemoryMarshal.GetReference(input), input.Length) != input.Length)
                {
                    // Make sure we clear out the error that is stored in the queue
                    throw Crypto.CreateOpenSslCryptographicException();
                }
            }

            int retVal = Ssl.SslDoHandshake(context);
            if (retVal != 1)
            {
                Exception? innerError;
                Ssl.SslErrorCode error = GetSslError(context, retVal, out innerError);

                if ((retVal != -1) || (error != Ssl.SslErrorCode.SSL_ERROR_WANT_READ))
                {
                    // Handshake failed, but even if the handshake does not need to read, there may be an Alert going out.
                    // To handle that we will fall-through the block below to pull it out, and we will fail after.
                    handshakeException = new SslException(SR.Format(SR.net_ssl_handshake_failed_error, error), innerError);
                    Crypto.ErrClearError();
                }
            }

            sendCount = Crypto.BioCtrlPending(context.OutputBio!);
            if (sendCount > 0)
            {
                sendBuf = new byte[sendCount];

                try
                {
                    sendCount = BioRead(context.OutputBio!, sendBuf, sendCount);
                }
                catch (Exception) when (handshakeException != null)
                {
                    // If we already have handshake exception, ignore any exception from BioRead().
                }
                finally
                {
                    if (sendCount <= 0)
                    {
                        // Make sure we clear out the error that is stored in the queue
                        Crypto.ErrClearError();
                        sendBuf = null;
                        sendCount = 0;
                    }
                }
            }

            if (handshakeException != null)
            {
                throw handshakeException;
            }

            bool stateOk = Ssl.IsSslStateOK(context);
            if (stateOk)
            {
                context.MarkHandshakeCompleted();
            }
            return stateOk;
        }

        internal static int Encrypt(SafeSslHandle context, ReadOnlySpan<byte> input, ref byte[] output, out Ssl.SslErrorCode errorCode)
        {
#if DEBUG
            ulong assertNoError = Crypto.ErrPeekError();
            Debug.Assert(assertNoError == 0, $"OpenSsl error queue is not empty, run: 'openssl errstr {assertNoError:X}' for original error.");
#endif
            errorCode = Ssl.SslErrorCode.SSL_ERROR_NONE;

            int retVal;
            Exception? innerError = null;

            retVal = Ssl.SslWrite(context, ref MemoryMarshal.GetReference(input), input.Length);

            if (retVal != input.Length)
            {
                errorCode = GetSslError(context, retVal, out innerError);
            }

            if (retVal != input.Length)
            {
                retVal = 0;

                switch (errorCode)
                {
                    // indicate end-of-file
                    case Ssl.SslErrorCode.SSL_ERROR_ZERO_RETURN:
                    case Ssl.SslErrorCode.SSL_ERROR_WANT_READ:
                        break;

                    default:
                        throw new SslException(SR.Format(SR.net_ssl_encrypt_failed, errorCode), innerError);
                }
            }
            else
            {
                int capacityNeeded = Crypto.BioCtrlPending(context.OutputBio!);

                if (output == null || output.Length < capacityNeeded)
                {
                    output = new byte[capacityNeeded];
                }

                retVal = BioRead(context.OutputBio!, output, capacityNeeded);

                if (retVal <= 0)
                {
                    // Make sure we clear out the error that is stored in the queue
                    Crypto.ErrClearError();
                }
            }

            return retVal;
        }

        internal static int Decrypt(SafeSslHandle context, Span<byte> buffer, out Ssl.SslErrorCode errorCode)
        {
#if DEBUG
            ulong assertNoError = Crypto.ErrPeekError();
            Debug.Assert(assertNoError == 0, $"OpenSsl error queue is not empty, run: 'openssl errstr {assertNoError:X}' for original error.");
#endif
            errorCode = Ssl.SslErrorCode.SSL_ERROR_NONE;

            BioWrite(context.InputBio!, buffer);

            int retVal = Ssl.SslRead(context, ref MemoryMarshal.GetReference(buffer), buffer.Length);
            if (retVal > 0)
            {
                return retVal;
            }

            errorCode = GetSslError(context, retVal, out Exception? innerError);
            switch (errorCode)
            {
                // indicate end-of-file
                case Ssl.SslErrorCode.SSL_ERROR_ZERO_RETURN:
                    break;

                case Ssl.SslErrorCode.SSL_ERROR_WANT_READ:
                    // update error code to renegotiate if renegotiate is pending, otherwise make it SSL_ERROR_WANT_READ
                    errorCode = Ssl.IsSslRenegotiatePending(context) ?
                                Ssl.SslErrorCode.SSL_ERROR_RENEGOTIATE :
                                Ssl.SslErrorCode.SSL_ERROR_WANT_READ;
                    break;

                default:
                    throw new SslException(SR.Format(SR.net_ssl_decrypt_failed, errorCode), innerError);
            }

            return 0;
        }

        internal static SafeX509Handle GetPeerCertificate(SafeSslHandle context)
        {
            return Ssl.SslGetPeerCertificate(context);
        }

        internal static SafeSharedX509StackHandle GetPeerCertificateChain(SafeSslHandle context)
        {
            return Ssl.SslGetPeerCertChain(context);
        }

        #endregion

        #region private methods

        private static void QueryUniqueChannelBinding(SafeSslHandle context, SafeChannelBindingHandle bindingHandle)
        {
            bool sessionReused = Ssl.SslSessionReused(context);
            int certHashLength = context.IsServer ^ sessionReused ?
                                 Ssl.SslGetPeerFinished(context, bindingHandle.CertHashPtr, bindingHandle.Length) :
                                 Ssl.SslGetFinished(context, bindingHandle.CertHashPtr, bindingHandle.Length);

            if (0 == certHashLength)
            {
                throw CreateSslException(SR.net_ssl_get_channel_binding_token_failed);
            }

            bindingHandle.SetCertHashLength(certHashLength);
        }

        [UnmanagedCallersOnly]
        private static int VerifyClientCertificate(int preverify_ok, IntPtr x509_ctx_ptr)
        {
            // Full validation is handled after the handshake in VerifyCertificateProperties and the
            // user callback.  It's also up to those handlers to decide if a null certificate
            // is appropriate.  So just return success to tell OpenSSL that the cert is acceptable,
            // we'll process it after the handshake finishes.
            const int OpenSslSuccess = 1;
            return OpenSslSuccess;
        }

        [UnmanagedCallersOnly]
        private static unsafe int AlpnServerSelectCallback(IntPtr ssl, byte** outp, byte* outlen, byte* inp, uint inlen, IntPtr arg)
        {
            *outp = null;
            *outlen = 0;
            IntPtr sslData =  Ssl.SslGetData(ssl);

            // reset application data to avoid dangling pointer.
            Ssl.SslSetData(ssl, IntPtr.Zero);

            GCHandle protocolHandle = GCHandle.FromIntPtr(sslData);
            if (!(protocolHandle.Target is List<SslApplicationProtocol> protocolList))
            {
                return Ssl.SSL_TLSEXT_ERR_ALERT_FATAL;
            }

            try
            {
                for (int i = 0; i < protocolList.Count; i++)
                {
                    var clientList = new Span<byte>(inp, (int)inlen);
                    while (clientList.Length > 0)
                    {
                        byte length = clientList[0];
                        Span<byte> clientProto = clientList.Slice(1, length);
                        if (clientProto.SequenceEqual(protocolList[i].Protocol.Span))
                        {
                            fixed (byte* p = &MemoryMarshal.GetReference(clientProto)) *outp = p;
                            *outlen = length;
                            return Ssl.SSL_TLSEXT_ERR_OK;
                        }

                        clientList = clientList.Slice(1 + length);
                    }
                }
            }
            catch
            {
                // No common application protocol was negotiated, set the target on the alpnHandle to null.
                // It is ok to clear the handle value here, this results in handshake failure, so the SslStream object is disposed.
                protocolHandle.Target = null;

                return Ssl.SSL_TLSEXT_ERR_ALERT_FATAL;
            }

            // No common application protocol was negotiated, set the target on the alpnHandle to null.
            // It is ok to clear the handle value here, this results in handshake failure, so the SslStream object is disposed.
            protocolHandle.Target = null;

            return Ssl.SSL_TLSEXT_ERR_ALERT_FATAL;
        }

        private static int BioRead(SafeBioHandle bio, byte[] buffer, int count)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(count >= 0);
            Debug.Assert(buffer.Length >= count);

            int bytes = Crypto.BioRead(bio, buffer, count);
            if (bytes != count)
            {
                throw CreateSslException(SR.net_ssl_read_bio_failed_error);
            }
            return bytes;
        }

        private static void BioWrite(SafeBioHandle bio, ReadOnlySpan<byte> buffer)
        {
            int bytes = Ssl.BioWrite(bio, ref MemoryMarshal.GetReference(buffer), buffer.Length);
            if (bytes != buffer.Length)
            {
                throw CreateSslException(SR.net_ssl_write_bio_failed_error);
            }
        }

        private static Ssl.SslErrorCode GetSslError(SafeSslHandle context, int result, out Exception? innerError)
        {
            ErrorInfo lastErrno = Sys.GetLastErrorInfo(); // cache it before we make more P/Invoke calls, just in case we need it

            Ssl.SslErrorCode retVal = Ssl.SslGetError(context, result);
            switch (retVal)
            {
                case Ssl.SslErrorCode.SSL_ERROR_SYSCALL:
                    // Some I/O error occurred
                    innerError =
                        Crypto.ErrPeekError() != 0 ? Crypto.CreateOpenSslCryptographicException() : // crypto error queue not empty
                        result == 0 ? new EndOfStreamException() : // end of file that violates protocol
                        result == -1 && lastErrno.Error != Error.SUCCESS ? new IOException(lastErrno.GetErrorMessage(), lastErrno.RawErrno) : // underlying I/O error
                        null; // no additional info available
                    break;

                case Ssl.SslErrorCode.SSL_ERROR_SSL:
                    // OpenSSL failure occurred.  The error queue contains more details, when building the exception the queue will be cleared.
                    innerError = Interop.Crypto.CreateOpenSslCryptographicException();
                    break;

                default:
                    // No additional info available.
                    innerError = null;
                    break;
            }
            return retVal;
        }

        private static void SetSslCertificate(SafeSslContextHandle contextPtr, SafeX509Handle certPtr, SafeEvpPKeyHandle keyPtr)
        {
            Debug.Assert(certPtr != null && !certPtr.IsInvalid, "certPtr != null && !certPtr.IsInvalid");
            Debug.Assert(keyPtr != null && !keyPtr.IsInvalid, "keyPtr != null && !keyPtr.IsInvalid");

            int retVal = Ssl.SslCtxUseCertificate(contextPtr, certPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_use_cert_failed);
            }

            retVal = Ssl.SslCtxUsePrivateKey(contextPtr, keyPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_use_private_key_failed);
            }

            //check private key
            retVal = Ssl.SslCtxCheckPrivateKey(contextPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_check_private_key_failed);
            }
        }

        internal static SslException CreateSslException(string message)
        {
            // Capture last error to be consistent with CreateOpenSslCryptographicException
            ulong errorVal = Crypto.ErrPeekLastError();
            Crypto.ErrClearError();
            string msg = SR.Format(message, Marshal.PtrToStringAnsi(Crypto.ErrReasonErrorString(errorVal)));
            return new SslException(msg, (int)errorVal);
        }

        #endregion

        #region Internal class

        internal sealed class SslException : Exception
        {
            public SslException(string? inputMessage)
                : base(inputMessage)
            {
            }

            public SslException(string? inputMessage, Exception? ex)
                : base(inputMessage, ex)
            {
            }

            public SslException(string? inputMessage, int error)
                : this(inputMessage)
            {
                HResult = error;
            }

            public SslException(int error)
                : this(SR.Format(SR.net_generic_operation_failed, error))
            {
                HResult = error;
            }
        }

        #endregion
    }
}
