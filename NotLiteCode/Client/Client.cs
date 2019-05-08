﻿using NotLiteCode.Network;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using static NotLiteCode.Helpers;

namespace NotLiteCode.Client
{
    public class Client
    {
        public ConcurrentDictionary<string, TiedEventWait> Callbacks = new ConcurrentDictionary<string, TiedEventWait>();
        public NLCSocket ClientSocket;
        public bool UseCallbacks;

        private readonly SemaphoreSlim CallSem = new SemaphoreSlim(1, 1);

        public Client() : this(new NLCSocket(), false)
        { }

        public Client(NLCSocket ClientSocket) : this(ClientSocket, false)
        { }

        public Client(bool UseCallbacks) : this(new NLCSocket(), UseCallbacks)
        { }

        public Client(NLCSocket ClientSocket, bool UseCallbacks)
        {
            this.ClientSocket = ClientSocket;
            this.UseCallbacks = UseCallbacks;

            if (UseCallbacks && ClientSocket.BaseSocket.Connected)
            {
                if (!ClientSocket.ContinueSubscribing)
                    ClientSocket.ContinueSubscribing = true;

                ClientSocket.OnNetworkMessageReceived += OnCallbackMessageReceived;
            }
        }

        public bool Connect(string ServerAddress, int ServerPort)
        {
            ClientSocket.Connect(ServerAddress, ServerPort);

            if (UseCallbacks)
            {
                ClientSocket.OnNetworkMessageReceived += OnCallbackMessageReceived;
                ClientSocket.BeginAcceptMessages();
            }

            return true;
        }

        private void OnCallbackMessageReceived(object sender, OnNetworkMessageReceivedEventArgs e)
        {
            if (e.Message.Header != NetworkHeader.HEADER_RETURN && e.Message.Header != NetworkHeader.HEADER_ERROR)
                throw new Exception("Invalid message type received!");

            Callbacks[e.Message.CallbackGuid].Result = e.Message;
            Callbacks[e.Message.CallbackGuid].Event.Set();
        }

        public void Stop()
        {
            ClientSocket.Close();
        }

        public async Task<T> RemoteCall<T>(string identifier, params object[] param)
        {
            if (UseCallbacks)
            {
                var CallbackGuid = Guid.NewGuid().ToString();

                Callbacks.TryAdd(CallbackGuid, new TiedEventWait());

                var Event = new NetworkEvent(NetworkHeader.HEADER_MOVE, CallbackGuid, identifier, param);

                if (!await ClientSocket.BlockingSend(Event))
                    throw new Exception("Failed to sent request to server!");

                Callbacks[CallbackGuid].Event.WaitOne();

                var Result = Callbacks[CallbackGuid].Result;

                Callbacks.TryRemove(CallbackGuid, out _);

                return (T)Result.Data;
            }
            else
            {
                await CallSem.WaitAsync();

                var Event = new NetworkEvent(NetworkHeader.HEADER_MOVE, identifier, param);

                if (!await ClientSocket.BlockingSend(Event))
                    throw new Exception("Failed to sent request to server!");

                NetworkEvent Result;

                if ((Result = await ClientSocket.BlockingReceive()) == default(NetworkEvent))
                    throw new Exception("Failed to receive result from server!");

                if (Result.Header == NetworkHeader.HEADER_ERROR)
                    throw new Exception("An exception was caused on the server!");
                else if (Result.Header != NetworkHeader.HEADER_RETURN)
                    throw new Exception("Unexpected error");

                CallSem.Release();

                return (T)Result.Data;
            }
        }

        public async Task RemoteCall(string identifier, params object[] param)
        {
            if (UseCallbacks)
            {
                var CallbackID = Guid.NewGuid().ToString();

                Callbacks.TryAdd(CallbackID, new TiedEventWait());

                var Event = new NetworkEvent(NetworkHeader.HEADER_CALL, CallbackID, identifier, param);

                if (!await ClientSocket.BlockingSend(Event))
                    throw new Exception("Failed to sent request to server!");

                Callbacks[CallbackID].Event.WaitOne();

                Callbacks.TryRemove(CallbackID, out _);
            }
            else
            {
                await CallSem.WaitAsync();

                var Event = new NetworkEvent(NetworkHeader.HEADER_CALL, identifier, param);

                if (!await ClientSocket.BlockingSend(Event))
                    throw new Exception("Failed to sent request to server!");

                NetworkEvent ReturnEvent;

                if ((ReturnEvent = await ClientSocket.BlockingReceive()) == default(NetworkEvent))
                    throw new Exception("Failed to receive result from server!");

                if (ReturnEvent.Header == NetworkHeader.HEADER_ERROR)
                    throw new Exception("An exception was caused on the server!");
                else if (ReturnEvent.Header != NetworkHeader.HEADER_RETURN)
                    throw new Exception("Unexpected error");

                CallSem.Release();
            }
        }
    }
}