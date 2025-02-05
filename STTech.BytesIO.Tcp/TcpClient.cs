﻿using STTech.BytesIO.Core;
using STTech.BytesIO.Tcp.Entity;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace STTech.BytesIO.Tcp
{
    /// <summary>
    /// TCP通信客户端
    /// </summary>
    public partial class TcpClient : BytesClient, ITcpClient
    {
        /// <summary>
        /// 内部TCP客户端
        /// </summary>
        protected System.Net.Sockets.TcpClient InnerClient { get; set; }

        /// <summary>
        /// 获取内部的TCP客户端
        /// </summary>
        /// <returns></returns>
        public System.Net.Sockets.TcpClient GetInnerClient() => InnerClient;

        /// <summary>
        /// 接受缓存区
        /// </summary>
        protected byte[] socketDataReceiveBuffer = null;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public override bool IsConnected => InnerClient != null && InnerClient.Client != null && InnerClient.Client.Connected;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public override int ReceiveBufferSize { get; set; } = 65536;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public override int SendBufferSize { get; set; } = 32768;

        /// <summary>
        /// 内部状态
        /// </summary>
        private InnerStatus innerStatus = InnerStatus.Free;

        /// <summary>
        /// 状态锁
        /// </summary>
        private object lockerStatus = new object();

        /// <summary>
        /// 构造TCP客户端
        /// </summary>
        public TcpClient()
        {
            InnerClient = new System.Net.Sockets.TcpClient();
        }

        /// <summary>
        /// 构造TCP客户端
        /// </summary>
        /// <param name="innerClient">内部的TCP客户端（<c>System.Net.Sockets.TcpClient</c>）</param>
        public TcpClient(System.Net.Sockets.TcpClient innerClient)
        {
            this.InnerClient = innerClient;

            if (innerClient.Connected)
            {
                // 设置内部状态为忙碌
                innerStatus = InnerStatus.Busy;

                IPEndPoint point = (IPEndPoint)innerClient.Client.RemoteEndPoint;
                Host = point.Address.ToString();
                Port = point.Port;
                socketDataReceiveBuffer = new byte[ReceiveBufferSize];
                // 启动接收数据的异步任务
                StartReceiveDataTask();
            }
        }

        /// <summary>
        /// 构造TCP客户端
        /// </summary>
        /// <param name="socket">内部的Socket对象</param>
        public TcpClient(Socket socket) : this(new System.Net.Sockets.TcpClient { Client = socket })
        {
        }

        /// <summary>
        /// 异步建立连接
        /// </summary>
        public override ConnectResult Connect(ConnectArgument argument = null)
        {
            argument ??= new ConnectArgument();

            lock (lockerStatus)
            {
                // 如果client已经连接了，则此次连接无效
                if (InnerClient.Connected || innerStatus == InnerStatus.Busy)
                {
                    RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.IsConnected));
                    return new ConnectResult(ConnectErrorCode.IsConnected);
                }

                try
                {
                    // 创建数据接收缓冲区
                    if (socketDataReceiveBuffer == null || ReceiveBufferSize != socketDataReceiveBuffer.Length)
                    {
                        socketDataReceiveBuffer = null;
                        socketDataReceiveBuffer = new byte[ReceiveBufferSize];
                    }

                    // 建立连接
                    InnerClient.ReceiveBufferSize = ReceiveBufferSize;
                    InnerClient.SendBufferSize = SendBufferSize;

                    // 连接是否完成（非超时）
                    var isComplete = InnerClient.ConnectAsync(Host, Port).Wait(argument.Timeout);

                    // 如果超时，则返回超时结果
                    if (!isComplete)
                    {
                        RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.Timeout));
                        return new ConnectResult(ConnectErrorCode.Timeout);
                    }

                    // 是否使用SSL/TLS通信
                    if (UseSsl)
                    {
                        try
                        {
                            // 创建SSL流
                            SslStream = new SslStream(InnerClient.GetStream(), false, RemoteCertificateValidationHandle ?? RemoteCertificateValidateCallback, LocalCertificateSelectionHandle ?? LocalCertificateSelectionCallback, EncryptionPolicy.AllowNoEncryption);
                            SslStream.AuthenticateAsClient(ServerCertificateName, new X509CertificateCollection(new X509Certificate[] { Certificate }), SslProtocol, false);

                            // 执行TLS通信验证通过的回调事件
                            PerformTlsVerifySuccessfully(this, new TlsVerifySuccessfullyEventArgs(SslStream));
                        }
                        catch (Exception ex)
                        {
                            // throw new Exception("SSL certificate validation failed.", ex);
                            RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.Error, ex));
                            return new ConnectResult(ConnectErrorCode.Error, ex);
                        }
                    }

                    // 设置内部状态为忙碌
                    innerStatus = InnerStatus.Busy;

                    // 执行连接成功回调事件
                    RaiseConnectedSuccessfully(this, new ConnectedSuccessfullyEventArgs());

                    // 启动接收数据的异步任务
                    StartReceiveDataTask();

                    return new ConnectResult();
                }
                catch (Exception ex)
                {
                    // 连接失败
                    RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ex));

                    // 重置tcp客户端
                    ResetInnerClient();

                    // 释放缓冲区
                    // socketDataReceiveBuffer = null;

                    // 返回操作错误结果
                    if (ex is SocketException socketEx)
                    {
                        switch (socketEx.SocketErrorCode)
                        {
                            case SocketError.HostNotFound:
                                RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.ConnectionParameterError, ex));
                                return new ConnectResult(ConnectErrorCode.ConnectionParameterError, ex);
                            default:
                                RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.Error, ex));
                                return new ConnectResult(ConnectErrorCode.Error, ex);
                        }
                    }
                    else
                    {
                        RaiseConnectionFailed(this, new ConnectionFailedEventArgs(ConnectErrorCode.Error, ex));
                        return new ConnectResult(ConnectErrorCode.Error, ex);
                    }
                }
            }
        }

        /// <summary>
        /// 重置内部客户端
        /// </summary>
        private void ResetInnerClient()
        {
#if NET45
            InnerClient.Client.Dispose();
#else
            InnerClient?.Dispose();
#endif
            InnerClient = new System.Net.Sockets.TcpClient();
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <param name="code"></param>
        /// <param name="ex"></param>
        public override DisconnectResult Disconnect(DisconnectArgument argument = null)
        {
            argument ??= new DisconnectArgument();

            lock (lockerStatus)
            {
                // 如果TcpClient没有关闭，则关闭连接
                if (InnerClient.Connected || innerStatus == InnerStatus.Busy)
                {
                    // 关闭异步任务
                    CancelReceiveDataTask();

                    // 关闭内部Socket客户端
                    InnerClient.Close();

                    // 重置TCP客户端
                    ResetInnerClient();

                    // 重置内部状态为空闲
                    innerStatus = InnerStatus.Free;

                    // 执行通信已断开的回调事件 
                    RaiseDisconnected(this, new DisconnectedEventArgs(argument.ReasonCode, argument.Exception));

                    return new DisconnectResult();
                }
                else
                {
                    // 当前无连接
                    return new DisconnectResult(DisconnectErrorCode.NoConnection);
                }
            }
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <param name="data"></param>
        protected override void SendHandler(byte[] data)
        {
            try
            {
                if (UseSsl)
                {
                    SslStream.Write(data);
                    SslStream.Flush();
                }
                else
                {
                    // 发送数据
                    InnerClient.Client.Send(data);
                }
                // 执行数据已发送的回调事件
                RaiseDataSent(this, new DataSentEventArgs(data));
            }
            catch (Exception ex)
            {
                // 通信异常
                RaiseExceptionOccurs(this, new ExceptionOccursEventArgs(ex));
            }
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        protected override void ReceiveDataHandle()
        {
            try
            {
                int CheckTimes = 0;
                Stream stream = UseSsl ? SslStream : InnerClient.GetStream();
                while (IsConnected)
                {
                    // 获取数据长度
                    int len = stream.Read(socketDataReceiveBuffer, 0, socketDataReceiveBuffer.Length);

                    // 截取有效数据
                    byte[] data = socketDataReceiveBuffer.Take(len).ToArray();

                    if (data.Length == 0)
                    {
                        // 连续5次接收到空数据 则看作通信已断开
                        if (++CheckTimes > 5)
                            return;
                        else
                            continue;
                    }
                    else
                    {
                        CheckTimes = 0;
                    }

                    InvokeDataReceivedEventCallback(data);
                }
            }
            catch (Exception ex)
            {
                // 如果关闭了通信，不回调异常
                if (!InnerClient.Connected)
                {
                    if (ex is IOException && ex.InnerException != null)
                    {
                        if (ex.InnerException is SocketException ex2)
                        {
                            switch (ex2.SocketErrorCode)
                            {
                                case SocketError.ConnectionReset:   // TODO: 待解决问题
                                case SocketError.Interrupted:       // TODO: 待解决问题"WSACancelBlockingCall"
                                    return;
                            }
                        }
                    }
                }

                // 回调异常事件
                RaiseExceptionOccurs(this, new ExceptionOccursEventArgs(ex));
            }
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        protected override void ReceiveDataCompletedHandle()
        {
            Disconnect(new DisconnectArgument(DisconnectionReasonCode.Passive));
        }

        /// <summary>
        /// 内部客户端的连接状态
        /// </summary>
        private enum InnerStatus
        {
            Free,
            Busy,
        }
    }

    public partial class TcpClient : ITcpClient
    {
        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public int Port { get; set; } = 8086;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public int LocalPort => IsConnected ? ((IPEndPoint)InnerClient.Client.LocalEndPoint).Port : 0;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public IPEndPoint RemoteEndPoint => (IPEndPoint)InnerClient.Client.RemoteEndPoint;
    }


}
