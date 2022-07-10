using NUnit.Framework;
using STTech.BytesIO.Tcp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace STTech.BytesIO.Test
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TcpConnectSendData()
        {
            AutoResetEvent evt = new AutoResetEvent(false);
            TcpClient client = new TcpClient() { Host = "127.0.0.1", Port = 60000 };
            client.OnConnectedSuccessfully += (s, e) => Debug.WriteLine("���ӳɹ�");
            client.OnConnectionFailed += (s, e) => Debug.WriteLine("����ʧ��");
            client.OnDisconnected += (s, e) => { Debug.WriteLine("���ӶϿ�"); evt.Reset(); };
            client.OnDataSent += (s, e) => Debug.WriteLine($"�������ݣ�{e.Data.ToHexString()} ({e.Data.EncodeToString()})");
            client.OnDataReceived += (s, e) => Debug.WriteLine($"�յ����ݣ�{e.Data.ToHexString()} ({e.Data.EncodeToString()})");
            client.ConnectAsync();
            client.ConnectAsync();
            client.ConnectAsync();

            Thread.Sleep(1000);

            if (client.IsConnected)
            {
                client.SendAsync("Hello World.".GetBytes());
                evt.WaitOne();
            }

            Assert.Pass();
        }
    }
}