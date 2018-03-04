﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fritz.StreamTools.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Fritz.StreamTools.Services.Mixer
{
	public interface IJsonRpcWebSocket
	{
		bool IsAuthenticated { get; }
		TimeSpan SendTimeout { get; set; }

		event EventHandler<EventEventArgs> EventReceived;
		Task<bool> SendAsync(string method, params object[] args);
		Task<bool> TryConnectAsync(Func<string> resolveUrl, string accessToken, Func<Task> postConnectFunc);
		void Dispose();
	}

	public class EventEventArgs : EventArgs
	{
		public string Event { get; set; }
		public JToken Data { get; set; }
	}

	internal class JsonRpcWebSocket : IJsonRpcWebSocket, IDisposable
	{
		const int CONNECT_TIMEOUT = 20000;  // In milliseconds
		const int SOCKET_BUFFER_SIZE = 1024;

		readonly ILogger _logger;
		readonly IMixerFactory _factory;
		readonly bool _isChat;
		readonly byte[] _receiveBuffer;

		IClientWebSocketProxy _ws;
		readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();
		bool _disposed;
		int _nextPacketId = 0;
		readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingRequests = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
		readonly ConcurrentQueue<string> _myLatestMessages = new ConcurrentQueue<string>();
		Task _receiverTask;
		int? _receiverThreadId;
		private readonly IConfiguration _config;

		public bool IsAuthenticated { get; private set; }
		public TimeSpan SendTimeout { get; set; }

		/// <summary>
		/// Raised each time an event is received on the websocket
		/// </summary>
		public event EventHandler<EventEventArgs> EventReceived;

		/// <summary>
		/// Construct a new JsonRpcWebSocket object
		/// </summary>
		public JsonRpcWebSocket(ILogger logger, IMixerFactory factory, IConfiguration config, bool isChat)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_factory = factory ?? throw new ArgumentNullException(nameof(factory));
			_isChat = isChat;
			_receiveBuffer = new byte[SOCKET_BUFFER_SIZE];
			SendTimeout = TimeSpan.FromSeconds(10);
			_config = config ?? throw new ArgumentNullException(nameof(config));
		}

		/// <summary>
		/// Try connecting to the specified websocker url once.
		/// If the connection is successfull, it will complete the function and you dont have to worry
		/// about reconnects
		/// </summary>
		public async Task<bool> TryConnectAsync(Func<string> resolveUrl, string accessToken, Func<Task> postConnectFunc)
		{
			if (resolveUrl == null)
				throw new ArgumentNullException(nameof(resolveUrl));
			if (_disposed) throw new ObjectDisposedException(nameof(JsonRpcWebSocket));

			// Local function that will try to reconnect forever
			async Task reconnect()
			{
				while (true)
				{
					// Connection lost, wait a little and try again
					await Task.Delay(GetReconnectDelay(), _cancellationToken.Token);

					await connect();
					if (_ws != null)
						return;
				}
			}

			// Local function used for connect and re-connects
			async Task connect()
			{
				_ws = null;
				var url = resolveUrl();

				try
				{
					var ws = CreateWebSocket(accessToken);

					// Connect the websocket
					_logger.LogInformation("Connecting to {0}", url);
					await ws.ConnectAsync(new Uri(url), _cancellationToken.Token).OrTimeout(CONNECT_TIMEOUT);
					_logger.LogInformation("Connected to {0}", url);
					_ws = ws;

					await EatWelcomeMessageAsync().OrTimeout(10000);

					// start receiving data
					_receiverTask = Task.Factory.StartNew(() => ReceiverTask(reconnect), TaskCreationOptions.LongRunning);

					if (postConnectFunc != null)
						await postConnectFunc();
				}
				catch (Exception e)
				{
					_ws = null;
					_logger.LogWarning("Connection to '{0}' failed: {1}", url, e.Message);
				}
			}

			// Do the first time connect
			await connect();
			return _ws != null;
		}

		private TimeSpan GetReconnectDelay()
		{
			var t = _config["StreamServices:Mixer:ReconnectDelay"];
			var delay = ( t != null ) ? TimeSpan.Parse(t) : TimeSpan.FromSeconds(5);
			if (delay < TimeSpan.FromMilliseconds(10))
				delay = TimeSpan.FromMilliseconds(10);
			return delay;
		}

		private IClientWebSocketProxy CreateWebSocket(string accessToken)
		{
			var ws = _factory.CreateClientWebSocket(_isChat);
			ws.SetRequestHeader("x-is-bot", "true");
			if (!string.IsNullOrEmpty(accessToken))
			{
				ws.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			}

			return ws;
		}

		private async Task EatWelcomeMessageAsync()
		{
			// Wait for next message
			var json = await ReceiveNextMessageAsync(_ws);
			if (string.IsNullOrEmpty(json))
			{
				_logger.LogWarning("Received null message EatWelcomeMessageAsync()");
				return;
			}

			_logger.LogTrace("<< " + json);
			var doc = JToken.Parse(json);
			if(doc["event"]?.Value<string>() == "hello")
			{
				var b = doc["data"]?["authenticated"]?.Value<bool>();
				IsAuthenticated = b.GetValueOrDefault();
			}
		}

		/// <summary>
		/// Received data from the websocket.
		/// This will run for the lifetime of the connection or until cancellation is requested
		/// </summary>
		private async Task ReceiverTask(Func<Task> reconnect)
		{
			_receiverThreadId = Thread.CurrentThread.ManagedThreadId;
			var ws = _ws;

			while (!_cancellationToken.IsCancellationRequested)
			{
				try
				{
					// Wait for next message
					var json = await ReceiveNextMessageAsync(ws);
					if (json == null)
						throw new Exception("Connection closed");
					_logger.LogTrace("<< " + json);

					ProcessReceivedMessage(json);

					ws.ProcessingDone(); // Dont remove! Will break tests
				}
				catch (Exception e)
				{
					_logger.LogWarning("Error in ReceiverTask() {0}. Will reconnect", e.Message);
					if (_cancellationToken.IsCancellationRequested) return;

					reconnect().Forget();  // Will spawn a new receiver task
					break;
				}
			}
		}

		private void ProcessReceivedMessage(string json)
		{
			var doc = JToken.Parse(json);
			if (doc.IsNullOrEmpty()) return;

			var type = doc["type"];
			if (type.IsNullOrEmpty()) return;

			switch ((string)type)
			{
				case "reply":
					HandleReply(doc);
					break;
				case "event":
					HandleEvent(doc);
					break;
			}
		}

		/// <summary>
		/// Handle an event message from the websocket
		/// </summary>
		private void HandleEvent(JToken doc)
		{
			if (doc.IsNullOrEmpty()) return;

			var data = doc["data"];
			if (data.IsNullOrEmpty()) return;
			if (data.Type != JTokenType.Object) return;

			if (!data["id"].IsNullOrEmpty())
			{
				// Ignore messages I have send
				var msgId = (string)data["id"];
				if (_myLatestMessages.Contains(msgId)) return;
			}

			// Some event received, chat message maybe ?
			EventReceived?.Invoke(this, new EventEventArgs { Event = doc["event"].Value<string>(), Data = data });
		}

		/// <summary>
		/// Handle a reply message from the websocket
		/// </summary>
		private void HandleReply(JToken doc)
		{
			if (doc.IsNullOrEmpty()) return;

			var error = doc["error"];
			if(!error.IsNullOrEmpty())
			{
				_logger.LogError($"Error from server: Code = {(int)error["code"]} Message = '{(string)error["message"]}'");
			}

			var data = doc["data"];
			if (data?.Type != JTokenType.Object) data = null;		// Ignore data which is not an object
			if (!data.IsNullOrEmpty() && !data["id"].IsNullOrEmpty())
			{
				// Remember last 5 messages I have send
				_myLatestMessages.Enqueue((string)data["id"]);
				while (_myLatestMessages.Count > 5) _myLatestMessages.TryDequeue(out var _);
			}

			if(!data.IsNullOrEmpty() && !data["authenticated"].IsNullOrEmpty())
			{
				IsAuthenticated = data["authenticated"].Value<bool>();
			}

			var id = doc["id"].Value<int>();
			if (_pendingRequests.TryGetValue(id, out var task))
			{
				// Signal waiting task that we have received a reply
				if (error != null && error.HasValues)
					task.SetResult(false);
				else
					task.SetResult(true);
			}
			else
			{
				_logger.LogWarning($"Received reply to unknown pending request (packet id={id}). We currently have {_pendingRequests.Count} pending request");
			}
		}

		/// <summary>
		/// Send a command to the server, will wait for reply
		/// </summary>
		/// <returns>true if success, or false if error</returns>
		public async Task<bool> SendAsync(string method, params object[] args)
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(JsonRpcWebSocket));
			if (Thread.CurrentThread.ManagedThreadId == _receiverThreadId)
				throw new Exception("Cannot call SendAsync on same thread as websocket receiver thread!");

			var ws = _ws;
			if (ws == null)
				return false;

			var id = Interlocked.Increment(ref _nextPacketId);
			var json = BuildRequestString(method, args, id);
			var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));

			// Send request and wait for reply (or timeout)
			var tcs = new TaskCompletionSource<bool>();
			_pendingRequests.TryAdd(id, tcs);

			try
			{
				await ws.SendAsync(buffer, WebSocketMessageType.Text, true, _cancellationToken.Token);
				if (Debugger.IsAttached)  // no timeout while debugging
					await tcs.Task;
				else
					await tcs.Task.OrTimeout(SendTimeout);
				return tcs.Task.Result;
			}
			finally
			{
				_pendingRequests.TryRemove(id, out var _);
			}
		}

		/// <summary>
		/// Builds the json request string for the server.
		/// Takes care of masking the authKey out when doing logging
		/// </summary>
		/// <param name="method">Method name</param>
		/// <param name="args">Variable arguments based on method</param>
		/// <param name="id">Id to use for the json rpc request</param>
		/// <returns>Json string</returns>
		private string BuildRequestString(string method, object[] args, int id)
		{
			var doc = new JObject
			{
				{ "id", id },
				{ "type", "method" },
				{ "method", method }
			};

			if (_isChat)
			{
				if (args != null && args.Length != 0)
					doc.Add(new JProperty("arguments", args));
			}
			else
			{
				doc.Add("params", JObject.FromObject(new { events = args }));
			}

			var json = doc.ToString(Newtonsoft.Json.Formatting.None);

			if (_logger.IsEnabled(LogLevel.Trace))
			{
				if (method == "auth" && args.Length >= 3)
				{
					// hide the authKey from log
					_logger.LogTrace(">> " + json.Replace((string)args[2], "************"));
				}
				else
				{
					_logger.LogTrace(">> " + json);
				}
			}

			return json;
		}

		/// <summary>
		/// Reads the complete next text message from the websocket
		/// </summary>
		/// <returns>The text message, or null if socket was closed</returns>
		private async Task<string> ReceiveNextMessageAsync(IClientWebSocketProxy ws)
		{
			var buffer = new ArraySegment<byte>(_receiveBuffer);
			WebSocketReceiveResult result;
			using (var ms = new MemoryStream())
			{
				do
				{
					result = await ws.ReceiveAsync(buffer, _cancellationToken.Token);
					if (result == null || result.Count == 0 || result.MessageType == WebSocketMessageType.Close) return null;
					if (ws.CloseStatus.HasValue) return null;
					Debug.Assert(result.MessageType == WebSocketMessageType.Text);
					ms.Write(buffer.Array, buffer.Offset, result.Count);
				}
				while (!result.EndOfMessage);

				ms.Seek(0, SeekOrigin.Begin);
				using (var reader = new StreamReader(ms, Encoding.UTF8))
					return reader.ReadToEnd();
			}
		}

		/// <summary>
		/// Closes the websocket connection and stops the receiving task
		/// </summary>
		public void Dispose()
		{
			if (_disposed) throw new ObjectDisposedException(nameof(JsonRpcWebSocket));

			// Stop receiver task
			_cancellationToken.Cancel();
			_ws?.Dispose();
			// Wait for it to complete
			_receiverTask?.Wait();

			_disposed = true;
		}
	}
}
