using UnityEngine;

using System;
using System.Collections;
using System.Net.Sockets;
using System.Net;

namespace Sockety
{
	enum ConnectionState {Initialized, Connecting, Connected};

	public class Sockety : MonoBehaviour
	{
		private ConnectionState state;

		private int CONNECT_TIMEOUT = 10; // seconds

		private string host;
		private int[] ports;

		private Socket socket;

		private int reconnectCount = 0;

		public string Host {
			get { return this.host; }
			set { this.host = value; }
		}

		public int[] Ports {
			get { return this.ports; }
			set { this.ports = value; }
		}

		public Sockety() {
			// lets initialize the Sockety.socket
			this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			this.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

			this.state = ConnectionState.Initialized;
		}

		public void Awake() {}

		public void Start()
		{
			StartCoroutine(this.Connect("127.0.0.1", 4567));
		}

		public void Update()
		{
			if (this.state == ConnectionState.Connected)
			{
				this.socket.Send(new byte[] {116, 101, 115, 116, 10});
			}
		}

		public IEnumerator Connect(string host, params int[] ports)
		{
			this.host = host;
			this.ports = ports;

			IPAddress ipAddress = this.GetIpForHost(host);

			this.state = ConnectionState.Connecting;

			var port = this.SelectPort();

			IAsyncResult result = this.socket.BeginConnect(new IPEndPoint(ipAddress, port), this.OnSocketConnect, this.socket);

			var connectStarted = Time.realtimeSinceStartup;

			while (!result.IsCompleted)
			{
				var connectingTime = Time.realtimeSinceStartup - connectStarted;
				if (connectingTime < CONNECT_TIMEOUT)
				{
					yield return null;
				}
				else
				{
					throw new Exception("Connection timed out.");
				}
			}

			// the async call has now completed, we need to check if errors occurred or not
			var connectedSocket = result.AsyncState as Socket;

			if (connectedSocket != this.socket)
			{
				throw new Exception("Sockets do not match.");
			}
		}

		private void OnSocketConnect(IAsyncResult result)
		{
			var connectedSocket = result.AsyncState as Socket;

			connectedSocket.EndConnect(result);

			this.state = ConnectionState.Connected;
		}

		private int SelectPort()
		{
			return this.ports[this.reconnectCount % this.ports.Length];
		}

		private IPAddress GetIpForHost(string host)
		{
			IPAddress ipAddress;

			if (!IPAddress.TryParse(host, out ipAddress))
			{
				// could not parse the host as IP address, do a DNS lookup
				// do note that this call is blocking so rather use direct IP addresses
				var ipHostInfo = Dns.GetHostEntry(host);
				foreach (var address in ipHostInfo.AddressList)
				{
					if (address.AddressFamily == AddressFamily.InterNetwork ||
					    address.AddressFamily == AddressFamily.InterNetworkV6)
					{
						ipAddress = address;
						break;
					}
				}
			}

			if (ipAddress == null)
			{
				throw new FormatException(String.Format("Could not get IP address for host '{0}'", host));
			}

			return ipAddress;
		}
	}
}

