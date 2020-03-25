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
/*
[System.Serializable]
public class UnityEvent_UdpPacket : UnityEvent <byte[]> {}

[System.Serializable]
public class UnityEvent_UdpHostname : UnityEvent <string> {}

[System.Serializable]
public class UnityEvent_UdpHostnameError : UnityEvent <string,string> {}

*/

[System.Serializable]
public class UnityEvent_UdpPort : UnityEvent<int> { }

[System.Serializable]
public class UnityEvent_PortError : UnityEvent<int, string> { }


//	this is the abstracted class, re-write for hololens
namespace PopX
{
	//	assume events are called NOT on the main thread
	public class UdpServerSocket : System.IDisposable
	{
		System.Net.Sockets.Socket Socket;
		System.Net.IPEndPoint ListeningEndPoint;
		//System.IAsyncResult RecvAsync;	//	do we store this so we can terminate on close?
		System.Action<byte[]> OnPacket;
		System.Action<string> OnCloseError;
		bool IsRunning = true;                //	false to stop thread/recv loop
		//System.Threading.Thread RecvThread;

		public UdpServerSocket(int Port, System.Action<byte[]> OnPacket, System.Action<int> OnListening, System.Action<string> OnCloseError)
		{
			//	todo: put some "unhandled" debug in these
			if (OnPacket == null)
				OnPacket = (Packet) => { };
			if (OnCloseError == null)
				OnCloseError = (Error) => { };
			if (OnListening == null)
				OnListening = (Portx) => { };

			this.OnPacket = OnPacket;
			this.OnCloseError = OnCloseError;

			ListeningEndPoint = new IPEndPoint(IPAddress.Any, Port);
			Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			Socket.Bind(ListeningEndPoint);

			//	help debug by not blocking
		//Socket.Blocking = false;

			var LocalEndPoint = (IPEndPoint)Socket.LocalEndPoint;
			OnListening.Invoke(LocalEndPoint.Port);
			StartRecv();
		}

		public void Dispose()
		{
			Close();
		}

		//	callback for async sending
		public void Send(byte[] Data, System.Action<bool> OnDataSent = null)
		{
			//	blocking atm, should make this async/threaded for large data
			//System.Net.Sockets.SocketFlags = SocketFlags.None;
			var SentBytes = Socket.Send(Data);
			if (SentBytes != Data.Length)
				throw new System.Exception("Only " + SentBytes + "/" + Data.Length + " bytes sent, todo handle this");
			if (OnDataSent != null)
				OnDataSent.Invoke(true);
		}

		public void Close(bool WaitForThread = true)
		{
			//	stop thread looping
			this.IsRunning = false;

			if (Socket != null)
			{
				//	to try and aid abort Recv, set it to non-blocking
				Debug.Log("Set socket nonblocking");
				Socket.Blocking = false;
				Debug.Log("Closing socket...");
				Socket.Close();
				Socket = null;
			}

			/*
			//	kill thread
			if (RecvThread != null)
			{
				Debug.Log("aborting thread...");
				//	I think we can safely abort, might need to check. If we don't, depending on how much data we've thrown at the decoder, this could take ages to finish
				RecvThread.Abort();
				RecvThread.Join();
				RecvThread = null;
			}
			*/
			//	can we kill pending async Recv?

			//	if socket was null, we should probably make sure that means we've already closed
			this.OnCloseError(null);
		}

		void StartRecv()
		{
			//	break the cycle if we're not running
			if (!IsRunning)
				return;

			//	gr: we've switched to async because we can't seem to interrupt RecvFrom() and thread gets stuck
			//		but this means the main thread is just gonna be interrupted? what thread is it on!
			/*
			if (RecvThread == null)
			{
				RecvThread = new System.Threading.Thread(new System.Threading.ThreadStart(RecvLoop));
				RecvThread.Start();
			}
			*/
			RecvIteration();
		}

		void OnRecv(object This_Socket, SocketAsyncEventArgs Args)
		{
			var Packet = Args.Buffer.SubArray(Args.Offset,Args.BytesTransferred);
			Debug.Log("Got Packet x"+Packet.Length + " Offset=" + Args.Offset);

			this.OnPacket(Packet);

			//	trigger another read
			StartRecv();
		}

		void RecvIteration()
		{
			Debug.Log("RecvIteration");

			const int BufferSize = 1024 * 1024;	//	won't ever be 1mb
			var Recv = new SocketAsyncEventArgs();
			Recv.Completed += OnRecv;
			Recv.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
			Recv.SetBuffer(new byte[BufferSize], 0, BufferSize);

			//	returns if is pending, if false, it completed synchronously
			if ( !Socket.ReceiveFromAsync(Recv))
			{
				OnRecv(this.Socket,Recv);
			}
		}

	}
}





public class PopUdpServer : MonoBehaviour
{
	[Header("First argument of event is port. Second is error")]
	public UnityEvent_UdpPort			OnConnecting;
	public UnityEvent_UdpPort 			OnListening;
	[Header("Invoked to match failed initial connect")]
	public UnityEvent_PortError			OnDisconnected;

	public UnityEvent_MessageBinary		OnMessageBinary;

	//	move these jobs to a thread!
	[Range(1,5000)]
	public int							MaxJobsPerFrame = 1000;

	PopX.UdpServerSocket Socket;
	bool		SocketConnecting = false;

	public bool	VerboseDebug = false;


	public List<int> Ports = new List<int>() { 0 };
	int				CurrentPortIndex = 0;
	public int 		CurrentPort	
	{	
		get
		{	
			try
			{
				return Ports[CurrentPortIndex % Ports.Count];
			}
			catch{}
			return 0;
		}
	}
		

	[Range(0,10)]
	public float	RetryTimeSecs = 5;
	private float	RetryTimeout = 1;

	//	websocket commands come on a different thread, so queue them for the next update
	List<System.Action>	JobQueue;


	public void Debug_Log(string Message)
	{
		//	if this is the main thread, we can just log, otherwise queue up
		Debug.Log (Message);
	}
			
	public void Listen(int NewPort)
	{	
		Ports = new List<int> (){ NewPort };
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

		var Port = CurrentPort;

		Debug_Log("Listening on " + Port + "...");
		OnConnecting.Invoke (Port);

		//	any failure from here should have a corresponding fail
		try
		{
			System.Action<int> OnOpen = (ListeningPort) =>
			{
				QueueJob (() => {
					Debug.Log("Listening " + Port + " (actual " + ListeningPort + ")");
					SocketConnecting = false;
					//Socket = NewSocket;
					OnListening.Invoke(ListeningPort);
				});
			};

			System.Action<string> OnClose = (Error) =>
			{
				QueueJob(() =>
				{
					SocketConnecting = false;
					OnError(Port, Error);
				});
			};

			System.Action<byte[]> OnPacket = (Packet) =>
			{
				QueueJob(() =>
				{
					OnBinaryMessage(Packet);
				});
			};

			SocketConnecting = true;
			Socket = new PopX.UdpServerSocket(Port, OnPacket, OnOpen, OnClose);
		}
		catch(System.Exception e) 
		{
			SocketConnecting = false;
			OnError(Port, e.Message);
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

	void OnError(int Port,string Message)
	{
		//SetStatus("Error: " + Message );
		Debug_Log(Port + " error: " + Message );
		OnDisconnected.Invoke (Port, Message);
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
