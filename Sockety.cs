using UnityEngine;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Linq;

namespace Sockety
{
	public enum ConnectionState
    {
        Initialized, // not yet connecting
        Connecting, // started to connect to given host
        Connected, // connected to given host
        TimedOut, // connection has timed out
        Error, // other socket error than timeout, e.g. connection refused
        Failed // not able to connect to any of the given ports, cannot recover from this
    };

	public enum Packet { OneByte, TwoBytes, FourBytes, Line, Custom };

	public class Sockety : MonoBehaviour
	{
		private ConnectionState state;

		private int CONNECT_TIMEOUT = 30; // this is the maximum time we connect over all given ports

		private static int BUFFER_SIZE = 1024 * 4;
        private const int NEWLINE = 10;

		private string host;
        private List<int> ports;
        private int currentPort; // the currently used port number

		private Socket socket;

		private byte[] receiveBuffer = new byte[BUFFER_SIZE];
        private int receiveBufferOffset = 0;

		private byte[] sendBuffer = new byte[BUFFER_SIZE];
        private int sendBufferOffset = 0;

        private Packet packetType = Packet.Line; 

		private bool receiving = false;

        private int backoff = 0; // the amount of times we tried to connect to all given ports
        private int portsTried = 0; //the amount of ports we've tried to connect to
        private int portsRemoved = 0; // the number of ports that had some error other than timeout
        private int timeoutCount = 0; // the number of connect timeouts

        private float connectStarted; // time when connect was called

        public Packet PacketType
        {
            get { return this.packetType; }
            set { this.packetType = value; }
        }

        public delegate void ReceivePacket(object packet);
        public event ReceivePacket OnReceivePacket;

		public Sockety()
        {
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

            this.Initialize(socket);
		}

        public Sockety(Socket socket)
        {
            this.Initialize(socket);
        }

        private void Initialize(Socket socket)
        {
            this.socket = socket;

            this.state = ConnectionState.Initialized;

            this.OnReceivePacket += (object packet) => {
                string line = packet as string;
                Debug.Log("Got line: " + line);
            };
        }

		public void Awake() {}

		public void Start()
		{
			StartCoroutine(this.Connect("127.0.0.1", 4567, 4568, 4569, 4570));
            StartCoroutine(this.TestSender());
		}

		public void Update()
		{
			if (this.state == ConnectionState.Connected)
			{
				if (!this.receiving)
				{
					Debug.Log ("Calling BeginReceive");
                    this.socket.BeginReceive (this.receiveBuffer, this.receiveBufferOffset, BUFFER_SIZE,
						SocketFlags.None, new AsyncCallback (this.ReceiveCallback), this.socket);
					this.receiving = true;
				}
			}
		}

		public IEnumerator Connect(string host, params int[] ports)
        {
            this.host = host;
            this.ports = new List<int>(ports);

            this.connectStarted = Time.realtimeSinceStartup;

            return this.ConnectToPort(this.SelectPort());
        }

        private IEnumerator ConnectToPort(int port)
        {
            Debug.Log(String.Format("Connecting to {0}:{1}", this.host, port));

            IPAddress ipAddress = this.GetIpForHost(host);

			this.state = ConnectionState.Connecting;

            var connectingStarted = Time.realtimeSinceStartup;

			IAsyncResult result = this.socket.BeginConnect(new IPEndPoint(ipAddress, port), this.BeginConnectCallback, this.socket);

            while (this.state == ConnectionState.Connecting)
            {
                var overallTime = Time.realtimeSinceStartup - this.connectStarted; // total time spent connecting
                var connectingTime = Time.realtimeSinceStartup - connectingStarted; // time tried connecting to one port

                if (overallTime > CONNECT_TIMEOUT)
                {
                    Debug.LogError("Connection timed out.");

                    this.state = ConnectionState.Failed;
                    this.socket.Close(); // cancel the ongoing connect
                }
                else if (connectingTime < this.GetConnectTimeout())
                {
                    yield return null;
                }
                else
                {
                    Debug.LogWarning(String.Format("Connection timed out to {0}:{1} after {2}s", this.host, port, connectingTime));
                    this.state = ConnectionState.TimedOut;
                    this.timeoutCount += 1;
                    this.ResetSocket();
                }
            }

            if (this.state == ConnectionState.Error || this.state == ConnectionState.TimedOut)
            {
                StartCoroutine(this.Reconnect());

                yield break; // end the previous connect coroutine
            }
            else if (this.state == ConnectionState.Failed)
            {
                throw new Exception("Failed to connect.");
            }
            else
            {
                var connectedSocket = result.AsyncState as Socket;

                if (connectedSocket != this.socket)
                {
                    throw new Exception("Sockets do not match.");
                }
            }
		}

        IEnumerator Reconnect()
        {
            // try next port
            this.portsTried += 1;

            // increase the connect timeout after all ports have timed out once
            if (this.ports.Count > 0 && (this.timeoutCount % this.ports.Count) == 0)
            {
                this.backoff += 1; // this will increase the connect timeout backoff
            }

            var port = this.SelectPort();

            return this.ConnectToPort(port);
        }

        IEnumerator TestSender()
        {
            while (true)
            {
                if (this.state == ConnectionState.Connected)
                {
                    yield return new WaitForSeconds(2);
                    this.Send("mitä kuuluu marjaleena?");
                }
                yield return null;
            }
        }

        public void Send(byte[] packet)
        {
            switch (this.packetType)
            {
                case Packet.Line:
                    this.SendLine(packet);
                    break;
                default:
                    break;
            }
        }

        public void Send(string line)
        {
            this.Send(Encoding.UTF8.GetBytes(line));
        }

		private void BeginConnectCallback(IAsyncResult result)
		{
            var resultSocket = result.AsyncState as Socket;

            try
            {
                resultSocket.EndConnect(result);

                this.state = ConnectionState.Connected;
            }
            catch (SocketException e)
            {
                Debug.LogWarning(e);

                // we remove a port that replied with error
                this.ports.Remove(this.currentPort);
                this.portsRemoved += 1;

                // close the socket
                this.ResetSocket();

                if (this.ports.Count == 0)
                {
                    this.state = ConnectionState.Failed;
                }
                else
                {
                    this.state = ConnectionState.Error;
                }
            }
            catch (ObjectDisposedException e)
            {
                // we end up here when canceling BeginReceive by closing the socket
                // nothing to do here
            }
		}

        private void ResetSocket()
        {
            if (this.socket != null)
            {
                this.socket.Close();
            }

            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        }

		private void ReceiveCallback(IAsyncResult result)
		{
			var receiveSocket = result.AsyncState as Socket;
			int readBytes = receiveSocket.EndReceive (result);

			Debug.Log ("Received " + readBytes + " bytes");

			switch (this.packetType)
			{
                case Packet.OneByte:
                    this.ProcessReceivedBytes(1, readBytes);
                    break;
                case Packet.TwoBytes:
                    this.ProcessReceivedBytes(2, readBytes);
                    break;
                case Packet.FourBytes:
                    this.ProcessReceivedBytes(4, readBytes);
                    break;
                case Packet.Line:
                    this.ProcessReceivedLine(readBytes);
                    break;
                default:
                    break;
			}

			this.receiving = false;
		}

		private void ProcessReceivedBytes (int headerSize, int readBytes)
		{
			for (int i = 0; i < readBytes; i++)
			{

			}
		}

        private void ProcessReceivedLine(int readBytes)
        {
            for (int i = 0; i < readBytes; i++)
            {
                byte receivedByte = this.receiveBuffer[i];
                if (receivedByte != NEWLINE) // check for newline
                {
                    this.receiveBufferOffset += 1;
                }
                else // we have a complete line
                {
                    byte[] lineBytes = new byte[this.receiveBufferOffset];
                    Array.Copy(this.receiveBuffer, lineBytes, this.receiveBufferOffset);

                    string lineString = Encoding.UTF8.GetString(lineBytes);

                    this.OnReceivePacket(lineString);

                    this.receiveBufferOffset = 0;
                }
            }
        }

        private void SendLine(byte[] packet)
        {
            Array.Copy(packet, 0, this.sendBuffer, this.sendBufferOffset, packet.Length);

            this.sendBuffer[this.sendBufferOffset + packet.Length] = NEWLINE;

            this.socket.BeginSend(this.sendBuffer, this.sendBufferOffset, packet.Length + 1,
                SocketFlags.None, new AsyncCallback(this.SendCallback), this.socket);
                
            this.sendBufferOffset = packet.Length + 1;
        }

        private void SendCallback(IAsyncResult result)
        {
            var sendSocket = result.AsyncState as Socket;
            int sentBytes = sendSocket.EndSend(result);

            // check that we were able to send all of the data
            if (sentBytes < this.sendBufferOffset)
            {
                // send the rest until the buffer is empty
                this.socket.BeginSend(this.sendBuffer, sentBytes, this.sendBufferOffset - sentBytes,
                    SocketFlags.None, new AsyncCallback(this.SendCallback), this.socket);
            }
            else
            {
                this.sendBufferOffset = 0;
            }
        }

		private int SelectPort()
		{
            var port = this.ports[(this.portsTried - this.portsRemoved) % this.ports.Count];

            string[] portsString = this.ports.ConvertAll<string>(x => x.ToString()).ToArray();

            Debug.Log("Ports: " + String.Join(",", portsString) + " Tried: " + this.portsTried + " Picked: " + port);

            this.currentPort = port;
            return port;
		}

        private int GetConnectTimeout()
        {
            // exponential backoff
            return Math.Min(1 << this.backoff + 1, CONNECT_TIMEOUT);
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

