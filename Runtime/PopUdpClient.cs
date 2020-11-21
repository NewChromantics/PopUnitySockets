using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using WebSocketSharp;

//	gr: for UWP/hololens, this needs to change to Windows.Networking & .DatagramSocket, I've written this before, but cannot find it...
//		todo: make a UWP UdpClient to match
using System.Net.Sockets;
using System.Net;

using UnityEngine.Events;

[System.Serializable]
public class UnityEvent_UdpPacket : UnityEvent <byte[]> {}

[System.Serializable]
public class UnityEvent_UdpHostname : UnityEvent <string> {}

[System.Serializable]
public class UnityEvent_UdpHostnameError : UnityEvent <string,string> {}


//	this is the abstracted class, re-write for hololens
namespace PopX
{
	//	assume events are called NOT on the main thread
	public class UdpSocket : System.IDisposable
	{
		System.Net.Sockets.UdpClient Socket;
		System.Net.IPEndPoint EndPoint;
		//System.IAsyncResult RecvAsync;	//	do we store this so we can terminate on close?
		System.Action<byte[]> OnPacket;
		System.Action<string> OnCloseError;

		public UdpSocket(string Hostname, int Port, System.Action<byte[]> OnPacket, System.Action OnConnected, System.Action<string> OnCloseError)
		{
			//	todo: put some "unhandled" debug in these
			if (OnPacket == null)
				OnPacket = (Packet) => { };
			if (OnCloseError == null)
				OnCloseError = (Error) => { };
			if (OnConnected == null)
				OnConnected = () => { };

			this.OnPacket = OnPacket;
			this.OnCloseError = OnCloseError;

			//	if constructor doesn't throw, we're connected
			//EndPoint = GetEndPoint(Hostname, Port);
			//Socket = new UdpClient(EndPoint);
			EndPoint = new IPEndPoint(IPAddress.Any, 0);
			Socket = new UdpClient(Hostname,Port);
			OnConnected.Invoke();
			StartRecv();
		}

		public void Dispose()
		{
			Close();
		}

		//	callback for async sending
		public void Send(byte[] Data, System.Action<bool> OnDataSent=null)
		{
			//	blocking atm, should make this async/threaded for large data
			var SentBytes = Socket.Send(Data, Data.Length);
			if (SentBytes != Data.Length)
				throw new System.Exception("Only " + SentBytes + "/" + Data.Length + " bytes sent, todo handle this");
			if (OnDataSent != null )
				OnDataSent.Invoke(true);
		}

		public void Close()
		{
			if ( Socket != null )
			{
				Socket.Close();
				Socket = null;
			}
			//	if socket was null, we should probably make sure that means we've already closed
			this.OnCloseError(null);
		}

		void StartRecv()
		{
			//	gr: does this throw if socket unreachable/closed?
			var RecvAsync = Socket.BeginReceive(new System.AsyncCallback(Recv),null);
		}

		void Recv(System.IAsyncResult Result)
		{
			byte[] Packet = null;
			//	this func gets invoked when socket is shutdown
			if (Result != null && Socket != null)
			{
				//	gr: does this throw if socket unreachable/closed?
				Packet = Socket.EndReceive(Result, ref EndPoint);
			}

			//	immediately start begin waiting for next one (some stackoverflow posts suggests a gap can drop packets!?)
			if ( Socket != null )
				StartRecv();

			if ( Packet != null)
				this.OnPacket(Packet);
		}

		/*
		IPEndPoint GetEndPoint(string Hostname,int Port)
		{
			//	todo; split port from hostname
			var Addresses = System.Net.Dns.GetHostAddresses(Hostname);
			if (Addresses.Length == 0)
				throw new System.Exception("Unable to resolve " + Hostname);

			//	todo; return multiple endpoints for each address
			if (Addresses.Length > 1)
				Debug.LogWarning("Hostname " + Hostname + " resolved to x" + Addresses.Length + " addresses. todo: support multiple tries with one hostname");

			var Address = Addresses[0];

			return new IPEndPoint(Address, Port);
		}
		*/
	}
}





public class PopUdpClient : MonoBehaviour
{
	[Header("First argument of event is host:port. Second is error")]
	public UnityEvent_Hostname			OnConnecting;
	public UnityEvent_Hostname			OnConnected;
	[Header("Invoked to match failed initial connect")]
	public UnityEvent_HostnameError		OnDisconnected;

	public UnityEvent_MessageBinary		OnMessageBinary;

	//	move these jobs to a thread!
	[Range(1,5000)]
	public int							MaxJobsPerFrame = 1000;

	public int	DefaultPort = 80;

	PopX.UdpSocket Socket;
	bool		SocketConnecting = false;

	public bool	VerboseDebug = false;


	public List<string>	Hosts = new List<string> (){ "localhost" };
	int					CurrentHostIndex = 0;
	public string 		CurrentHost	
	{	
		get
		{	
			try
			{
				return Hosts[CurrentHostIndex%Hosts.Count];
			}
			catch{}
			return null;
		}
	}
		

	[Range(0,10)]
	public float	RetryTimeSecs = 5;
	private float	RetryTimeout = 1;

	//	websocket commands come on a different thread, so queue them for the next update
	List<System.Action>	JobQueue;

	static readonly char[] HostAndPortSplitChars = { ':' };
	void SplitHostnameAndPort(string HostnameAndPort,out string Hostname,out int Port)
	{
		var HostAndPort = HostnameAndPort.Split(HostAndPortSplitChars, 2);
		Hostname = HostAndPort[0];

		//	no second string(port), return default
		if (HostAndPort.Length == 1)
		{
			Port = DefaultPort;
			return;
		}

		//	2nd string should be int
		Port = int.Parse(HostAndPort[1]);
	}


	public void Debug_Log(string Message)
	{
		//	if this is the main thread, we can just log, otherwise queue up
		Debug.Log (Message);
	}
			
	public void Connect(string NewHost)
	{	
		Hosts = new List<string> (){	NewHost };
		Connect ();
	}



	//	gr: change this to throw on immediate error
	void Connect()
	{	
		//	already connected
		if ( Socket != null )
			return;

		if ( SocketConnecting )
			return;

		var Host = CurrentHost;
		if (Host == null) {
			OnDisconnected.Invoke (null, "No hostname specified");
			return;
		}

		Debug_Log("Connecting to " + Host + "...");
		OnConnecting.Invoke (Host);

		//	any failure from here should have a corresponding fail
		try
		{
			System.Action OnOpen = () =>
			{
				QueueJob (() => {
					var HelloBytes = System.Text.Encoding.ASCII.GetBytes("hello");
					Socket.Send(HelloBytes);
					Debug.Log("Connected");
					SocketConnecting = false;
					//Socket = NewSocket;
					OnConnected.Invoke(Host);
				});
			};

			System.Action<string> OnClose = (Error) =>
			{
				QueueJob(() =>
				{
					SocketConnecting = false;
					OnError(Host, Error);
				});
			};

			System.Action<byte[]> OnPacket = (Packet) =>
			{
				QueueJob(() =>
				{
					OnBinaryMessage(Packet);
				});
			};

			string Hostname;
			int Port;
			SplitHostnameAndPort(Host, out Hostname, out Port);
			SocketConnecting = true;
			Socket = new PopX.UdpSocket(Hostname, Port, OnPacket, OnOpen, OnClose);
		}
		catch(System.Exception e) 
		{
			SocketConnecting = false;
			OnError(Host, e.Message);
		}
	}

	void Update()
	{

		/*
		if (Socket != null && !Socket.IsAlive) {
			OnError ("Socket not alive");
			Socket.Close ();
			Socket = null;
		}
*/
		if (Socket == null ) {

			if (RetryTimeout <= 0) {
				Connect ();
				RetryTimeout = RetryTimeSecs;
			} else {
				RetryTimeout -= Time.deltaTime;
			}
		}
	
		//	commands to execute from other thread
		if (JobQueue != null) {
			var JobsExecutedCount = 0;
			while (JobQueue.Count > 0 && JobsExecutedCount++ < MaxJobsPerFrame ) {

				if ( VerboseDebug )
					Debug.Log("Executing job 0/" + JobQueue.Count);
				var Job = JobQueue [0];
				JobQueue.RemoveAt (0);
				try
				{
					Job.Invoke ();
					if (VerboseDebug)
						Debug.Log("Job Done.");
				}
				catch(System.Exception e)
				{
					Debug.Log("Job invoke exception: " + e.Message );
				}
			}

			if (JobQueue.Count>0) {
				Debug.LogWarning("Executed " + JobsExecutedCount + " this frame, " + JobQueue.Count + " jobs remaining");
			}
		}

	}

	public void OnBinaryMessage(byte[] Message)
	{
		if (VerboseDebug)
			Debug_Log ("Binary Message: " + Message.Length + " bytes");
		OnMessageBinary.Invoke (Message);
	}

	void OnError(string Host,string Message)
	{
		//SetStatus("Error: " + Message );
		Debug_Log(Host + " error: " + Message );
		OnDisconnected.Invoke (Host, Message);
		SocketConnecting = false;

		if (Socket != null) 
		{
			//	pop variable to avoid recursion
			var s = Socket;
			Socket = null;
			s.Close ();
			s = null;
		}
	}


	void OnApplicationQuit()
	{
		if (Socket != null)
		{
			Socket.Close ();
			Socket = null;
		}
		
	}

	void QueueJob(System.Action Job)
	{
		if (JobQueue == null)
			JobQueue = new List<System.Action> ();
		JobQueue.Add( Job );
	}

	public void Send(byte[] Data,System.Action<bool> OnDataSent=null)
	{
		//	todo: queue packets
		if (Socket == null)
			throw new System.Exception ("Not connected");

		Socket.Send (Data,OnDataSent);
	}

}
