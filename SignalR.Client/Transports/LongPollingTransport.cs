﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SignalR.Client.Http;
using SignalR.Client.Infrastructure;
#if NET20
using Newtonsoft.Json.Serialization;
using SignalR.Client.Net20.Infrastructure;
#endif

namespace SignalR.Client.Transports
{
    public class LongPollingTransport : HttpBasedTransport
    {
        private static readonly TimeSpan _errorDelay = TimeSpan.FromSeconds(2);

        public TimeSpan ReconnectDelay { get; set; }

        public LongPollingTransport()
            : this(new DefaultHttpClient())
        {
        }

        public LongPollingTransport(IHttpClient httpClient)
            : base(httpClient, "longPolling")
        {
            ReconnectDelay = TimeSpan.FromSeconds(5);
        }

        protected override void OnStart(IConnection connection, string data, Action initializeCallback, Action<Exception> errorCallback)
        {
            PollingLoop(connection, data, initializeCallback, errorCallback);
        }

        private void PollingLoop(IConnection connection, string data, Action initializeCallback, Action<Exception> errorCallback, bool raiseReconnect = false)
        {
            string url = connection.Url;

            var reconnectInvoker = new ThreadSafeInvoker();
            var callbackInvoker = new ThreadSafeInvoker();

            if (connection.MessageId == null)
            {
                url += "connect";
            }
            else if (raiseReconnect)
            {
                url += "reconnect";

                if (connection.State != ConnectionState.Reconnecting &&
                    !connection.ChangeState(ConnectionState.Connected, ConnectionState.Reconnecting))
                {
                    return;
                }
            }

            url += GetReceiveQueryString(connection, data);

#if NET35 || NET20
            Debug.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "LP: {0}", (object)url));
#else
            Debug.WriteLine("LP: {0}", (object)url);
#endif

			_httpClient.PostAsync(url, PrepareRequest(connection), new Dictionary<string, string>{{"groups",GetGroupsAsString(connection)}}).ContinueWith(task =>
            {
                // Clear the pending request
                connection.Items.Remove(HttpRequestKey);

                bool shouldRaiseReconnect = false;
                bool disconnectedReceived = false;

                try
                {
                    if (!task.IsFaulted)
                    {
                        if (raiseReconnect)
                        {
                            // If the timeout for the reconnect hasn't fired as yet just fire the 
                            // event here before any incoming messages are processed
                            reconnectInvoker.Invoke((conn) => FireReconnected(conn), connection);
                        }

                        // Get the response
                        var raw = task.Result.ReadAsString();

#if NET35 || NET20
                        Debug.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "LP Receive: {0}", (object)raw));
#else
                        Debug.WriteLine("LP Receive: {0}", (object)raw);
#endif

                        ProcessResponse(connection, raw, out shouldRaiseReconnect, out disconnectedReceived);
                    }
                }
                finally
                {
                    if (disconnectedReceived)
                    {
                        connection.Stop();
                    }
                    else
                    {
                        bool requestAborted = false;

                        if (task.IsFaulted)
                        {
                            reconnectInvoker.Invoke();

                            // Raise the reconnect event if we successfully reconnect after failing
                            shouldRaiseReconnect = true;
                            
                            // Get the underlying exception
#if NET20
							Exception exception = ExceptionsExtensions.Unwrap(task.Exception);
#else
                            Exception exception = task.Exception.Unwrap();
#endif

                            // If the error callback isn't null then raise it and don't continue polling
                            if (errorCallback != null)
                            {
                                callbackInvoker.Invoke((cb, ex) => cb(ex), errorCallback, exception);
                            }
                            else
                            {
                                // Figure out if the request was aborted
                                requestAborted = ExceptionHelper.IsRequestAborted(exception);

                                // Sometimes a connection might have been closed by the server before we get to write anything
                                // so just try again and don't raise OnError.
                                if (!requestAborted && !(exception is IOException))
                                {
                                    // Raise on error
                                    connection.OnError(exception);

                                    // If the connection is still active after raising the error event wait for 2 seconds
                                    // before polling again so we aren't hammering the server 
#if NET20
                                    TaskAsyncHelper.Delay(_errorDelay).Then(_ =>
#else
                                    TaskAsyncHelper.Delay(_errorDelay).Then(() =>
#endif
                                    {
                                        if (connection.State != ConnectionState.Disconnected)
                                        {
                                            PollingLoop(connection,
                                                data,
                                                initializeCallback: null,
                                                errorCallback: null,
                                                raiseReconnect: shouldRaiseReconnect);
                                        }
                                    });
                                }
                            }
                        }
                        else
                        {
                            if (connection.State != ConnectionState.Disconnected)
                            {
                                // Continue polling if there was no error
                                PollingLoop(connection,
                                            data,
                                            initializeCallback: null,
                                            errorCallback: null,
                                            raiseReconnect: shouldRaiseReconnect);
                            }
                        }
                    }
                }
            });

            if (initializeCallback != null)
            {
                callbackInvoker.Invoke(initializeCallback);
            }

            if (raiseReconnect)
            {
#if NET20
                TaskAsyncHelper.Delay(ReconnectDelay).Then(_ =>
#else
                TaskAsyncHelper.Delay(ReconnectDelay).Then(() =>
#endif
                {
                    // Fire the reconnect event after the delay. This gives the 
                    reconnectInvoker.Invoke((conn) => FireReconnected(conn), connection);
                });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private static void FireReconnected(IConnection connection)
        {
            // Mark the connection as connected
            if (connection.ChangeState(ConnectionState.Reconnecting, ConnectionState.Connected))
            {
                connection.OnReconnected();
            }
        }
    }
}
