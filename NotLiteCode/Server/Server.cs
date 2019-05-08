﻿using NotLiteCode.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NotLiteCode.Server
{
    public class Server<T> where T : IDisposable, new()
    {
        private void NetworkClientDisconnected(object sender, OnNetworkClientDisconnectedEventArgs e) =>
          OnServerClientDisconnected?.Start(this, new OnServerClientDisconnectedEventArgs(e.Client));

        private void NetworkExceptionOccurred(object sender, OnNetworkExceptionOccurredEventArgs e) =>
          OnServerExceptionOccurred?.Start(this, new OnServerExceptionOccurredEventArgs(e.Exception));

        public event EventHandler<OnServerClientDisconnectedEventArgs> OnServerClientDisconnected;

        public event EventHandler<OnServerExceptionOccurredEventArgs> OnServerExceptionOccurred;

        public event EventHandler<OnServerClientConnectedEventArgs> OnServerClientConnected;

        public event EventHandler<OnServerMethodInvokedEventArgs> OnServerMethodInvoked;

        private readonly Dictionary<string, RemotingMethod> RemotingMethods = new Dictionary<string, RemotingMethod>();

        public Dictionary<EndPoint, RemoteClient<T>> Clients = new Dictionary<EndPoint, RemoteClient<T>>();

        public NLCSocket ServerSocket;

        public Server() : this(new NLCSocket())
        { }

        public Server(NLCSocket ServerSocket)
        {
            this.ServerSocket = ServerSocket;
            RegisterFunctions();
        }

        private void RegisterFunctions()
        {
            foreach (MethodInfo SharedMethod in typeof(T).GetMethods())
            {
                if (SharedMethod.GetCustomAttribute(typeof(NLCCall)) is NLCCall NLCAttribute)
                {
                    if (RemotingMethods.ContainsKey(NLCAttribute.Identifier))
                    {
                        OnServerExceptionOccurred?.Start(this, new OnServerExceptionOccurredEventArgs(new Exception($"Method with identifier {NLCAttribute.Identifier} already exists!")));
                        continue;
                    }

                    var IsAsync = SharedMethod.GetCustomAttribute(typeof(AsyncStateMachineAttribute)) is AsyncStateMachineAttribute;
                    var ReturnType = SharedMethod.ReturnType;
                    var HasAsyncResult = ReturnType.IsGenericType && ReturnType.GetGenericTypeDefinition() == typeof(Task<>);

                    if (!HasAsyncResult && IsAsync && ReturnType != typeof(Task))
                        throw new Exception($"Shared function {NLCAttribute.Identifier} is asynchronous but does not return a Task!");

                    RemotingMethods.Add(NLCAttribute.Identifier, new RemotingMethod() {
                        Method = Helpers.CreateMethodWrapper(typeof(T), SharedMethod),
                        WithContext = NLCAttribute.WithContext,
                        HasAsyncResult = HasAsyncResult
                    });
                }
            }
        }

        public void Start(int Port = 1337)
        {
            ServerSocket.OnNetworkExceptionOccurred += (x, y) => OnServerExceptionOccurred?.Start(this, new OnServerExceptionOccurredEventArgs(y.Exception));
            ServerSocket.OnNetworkClientConnected += OnNetworkClientConnected;

            ServerSocket.Listen(Port);
        }

        public void Stop()
        {
            ServerSocket.Close();
        }

        public void ManuallyConnectSocket(NLCSocket Socket)
        {
            OnNetworkClientConnected(null, new OnNetworkClientConnectedEventArgs(Socket));
        }

        private void OnNetworkClientConnected(object sender, OnNetworkClientConnectedEventArgs e)
        {
            var Client = new RemoteClient<T>(e.Client);
            e.Client.OnNetworkClientDisconnected += NetworkClientDisconnected;
            e.Client.OnNetworkExceptionOccurred += NetworkExceptionOccurred;
            e.Client.OnNetworkMessageReceived += OnNetworkMessageReceived;

            Clients.Add(e.Client.BaseSocket.RemoteEndPoint, Client);

            if(!e.Client.IsListening)
                e.Client.BeginAcceptMessages();

            OnServerClientConnected?.Start(this, new OnServerClientConnectedEventArgs(e.Client.BaseSocket.RemoteEndPoint));
        }

        public void DetatchFromSocket(NLCSocket Socket)
        {
            Socket.OnNetworkClientDisconnected -= NetworkClientDisconnected;
            Socket.OnNetworkExceptionOccurred -= NetworkExceptionOccurred;
            Socket.OnNetworkMessageReceived -= OnNetworkMessageReceived;
        }

        private async void OnNetworkMessageReceived(object sender, OnNetworkMessageReceivedEventArgs e)
        {
            if (e.Message.Header != NetworkHeader.HEADER_CALL && e.Message.Header != NetworkHeader.HEADER_MOVE)
                return;

            var RemoteEndPoint = ((NLCSocket)sender).BaseSocket.RemoteEndPoint;

            object Result = null;
            NetworkHeader ResultHeader;

            if (!RemotingMethods.TryGetValue(e.Message.Tag, out var TargetMethod))
            {
                OnServerExceptionOccurred?.Start(this, new OnServerExceptionOccurredEventArgs(new Exception("Client attempted to invoke invalid function!")));
                ResultHeader = NetworkHeader.NONE;
            }
            else
            {
                var Parameters = new List<object>();

                if (TargetMethod.WithContext)
                    Parameters.Add(RemoteEndPoint);

                Parameters.AddRange((object[])e.Message.Data);

                TimeSpan Duration = default(TimeSpan);

                try
                {
                    var sw = Stopwatch.StartNew();

                    Result = await TargetMethod.Method.InvokeWrapper(TargetMethod.HasAsyncResult, Clients[RemoteEndPoint].SharedClass, Parameters.ToArray());

                    sw.Stop();

                    Duration = sw.Elapsed;

                    ResultHeader = NetworkHeader.HEADER_RETURN;
                }
                catch (Exception ex)
                {
                    OnServerExceptionOccurred?.Start(this, new OnServerExceptionOccurredEventArgs(ex));
                    ResultHeader = NetworkHeader.HEADER_ERROR;
                }

                OnServerMethodInvoked?.Start(this, new OnServerMethodInvokedEventArgs(RemoteEndPoint, e.Message.Tag, Duration, ResultHeader == NetworkHeader.HEADER_ERROR));
            }

            var Event = new NetworkEvent(ResultHeader, e.Message.CallbackGuid, null, Result);

            await ((NLCSocket)sender).BlockingSend(Event);
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class NLCCall : Attribute
    {
        public readonly string Identifier;
        public readonly bool WithContext;

        public NLCCall(string Identifier, bool WithContext = false)
        {
            this.Identifier = Identifier;
            this.WithContext = WithContext;
        }
    }

    public struct RemotingMethod
    {
        public Func<object, object[], object> Method;
        public bool WithContext;
        public bool HasAsyncResult;
    }

    public class RemoteClient<T> where T : IDisposable, new()
    {
        public NLCSocket Socket;
        public T SharedClass;

        public RemoteClient(NLCSocket Socket)
        {
            this.Socket = Socket;
            this.SharedClass = new T();
        }
    }
}