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
	class FewerReallocBuffer
	{
		public int MaxOffset = 1033 * 100;  //	dealloc after X packets
		public int MaxAlloc = 20 * 1024 * 1024;	//	XMB
		int StartOffset = 0;
		List<byte> Buffer = new List<byte>();
		List<int> PacketLengths = new List<int>();

		public void Push(byte[] Data)
		{
			lock (Buffer)
			{
				if ( Buffer.Count > MaxAlloc )
				{
					Debug.LogError("Packet buffer has exceeded " + (MaxAlloc / 1024 / 1024) + "mb. Dropping all");
					PacketLengths = new List<int>();
					Buffer.RemoveRange(0, Buffer.Count);
					StartOffset = 0;
				}
				//	todo: expand buffer and overwrite instead of resize
				Buffer.AddRange(Data);
				PacketLengths.Add(Data.Length);
			}
		}

		public int GetPendingPackets()
		{
			return PacketLengths.Count;
		}

		public byte[] PopPacket()
		{
			//	no data
			if (PacketLengths.Count == 0)
			{
				//	todo: check buffer & offset here
				return null;
			}

			byte[] Packet;
			//	the lock is required to sync data on different threads
			//	the Threaded reciever though, can call this so often, that the lock on Pop blocks the main thread
			//	and makes unity stutter... i guess a trylock in the push and a sleep would help, to give this thread the
			//	priority
			lock (Buffer)
			{
				var Start = StartOffset;
				var Length = PacketLengths[0];
				Packet = new byte[Length];
				Buffer.CopyTo(Start, Packet, 0, Length);

				//	remove data
				StartOffset += Length;
				PacketLengths.RemoveAt(0);

				//	flush data
				if (StartOffset >= MaxOffset)
				{
					Buffer.RemoveRange(0, StartOffset);
					StartOffset = 0;
				}				
			}
			//	deadlock if we return inside the lock?!
			return Packet;
		}
	}

	//	assume events are called NOT on the main thread
	//	gr: this version uses async callback funcs
	public abstract class UdpServerSocket_Base : System.IDisposable
	{
		protected System.Net.Sockets.Socket Socket;
		protected System.Net.IPEndPoint ListeningEndPoint;
		System.Action OnPacketReady;					//	notify when a packet is availible to wake up threads etc
		protected System.Action<string> OnCloseError;
		protected bool IsRunning = true;    //	false to stop thread/recv loop

		//	to avoid dropping packets in UDP, we read as fast as possible and buffer up the output
		//	instead of a callback to send on packets (called ON the read thread) we notify and
		//	let the caller pop packets
		FewerReallocBuffer PacketBuffer = new FewerReallocBuffer();
		FrameCounter RecvKbCounter;

		public UdpServerSocket_Base(int Port, System.Action OnPacketReady,System.Action<int> OnListening, System.Action<string> OnCloseError, System.Action<float> OnKbpsReport)
		{
			if (OnKbpsReport == null)
				OnKbpsReport = (Kbps) => Debug.Log("UDP Server Recieved " + Kbps + "kb/s");
		
			//	todo: put some "unhandled" debug in these
			if (OnPacketReady == null)
				OnPacketReady = () => { };
			if (OnCloseError == null)
				OnCloseError = (Error) => { };
			if (OnListening == null)
				OnListening = (Portx) => { };

			RecvKbCounter = new FrameCounter(OnKbpsReport,1.0f);

			this.OnPacketReady = OnPacketReady;
			this.OnCloseError = OnCloseError;

			ListeningEndPoint = new IPEndPoint(IPAddress.Any, Port);
			Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			Socket.Bind(ListeningEndPoint);
			var ReceiveBufferSize = Socket.ReceiveBufferSize;
			Debug.Log("Socket ReceiveBufferSize="+ReceiveBufferSize);

			var LocalEndPoint = (IPEndPoint)Socket.LocalEndPoint;
			OnListening.Invoke(LocalEndPoint.Port);
			StartRecv();
		}

		public void Dispose()
		{
			Close();
		}
		
		public int GetPendingPackets()
		{
			return PacketBuffer.GetPendingPackets();
		}

		public byte[] PopPacket()
		{
			return PacketBuffer.PopPacket();
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
			//	if socket was null, we should probably make sure that means we've already closed
			this.OnCloseError(null);
		}

		abstract protected void StartRecv();


		protected void OnRecvPacket(byte[] Buffer)
		{
			RecvKbCounter.Add(Buffer.Length / 1024.0f);
			PacketBuffer.Push(Buffer);
			this.OnPacketReady();
		}
	}


	//	gr: this version uses async callback funcs
	public class UdpServerSocket_Async : UdpServerSocket_Base
	{
		public UdpServerSocket_Async(int Port, System.Action OnPacketReady, System.Action<int> OnListening, System.Action<string> OnCloseError, System.Action<float> OnKbpsReport) :
			base( Port, OnPacketReady, OnListening, OnCloseError, OnKbpsReport)
		{
		}

		override protected void StartRecv()
		{
			//	break the cycle if we're not running
			if (!IsRunning)
				return;

			RecvIteration();
		}

		void OnRecv(object This_Socket, SocketAsyncEventArgs Args)
		{
			var Packet = Args.Buffer.SubArray(Args.Offset,Args.BytesTransferred);
			//Debug.Log("Got Packet x"+Packet.Length + " Offset=" + Args.Offset);

			OnRecvPacket(Packet);

			//	trigger another read
			StartRecv();
		}

		void RecvIteration()
		{
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

	public class UdpServerSocket_Threaded : UdpServerSocket_Base
	{
		System.Threading.Thread RecvThread;

		public UdpServerSocket_Threaded(int Port, System.Action OnPacketReady, System.Action<int> OnListening, System.Action<string> OnCloseError, System.Action<float> OnKbpsReport) :
			base(Port, OnPacketReady, OnListening, OnCloseError,OnKbpsReport)
		{
		}


		override protected void StartRecv()
		{
			//	break the cycle if we're not running
			if (!IsRunning)
				return;

			//	if no thread, start it
			if (RecvThread == null)
			{
				//	gr: we've switched to async because we can't seem to interrupt RecvFrom() and thread gets stuck
				//		but this means the main thread is just gonna be interrupted? what thread is it on!
				RecvThread = new System.Threading.Thread(new System.Threading.ThreadStart(RecvLoop));
				RecvThread.Start();
			}
		}


		public void CloseThreadedSocket(bool WaitForThread = true)
		{
			this.IsRunning = false;

			//	kill thread
			if (RecvThread != null)
			{
				Debug.Log("aborting thread...");
				//	I think we can safely abort, might need to check. If we don't, depending on how much data we've thrown at the decoder, this could take ages to finish
				RecvThread.Abort();
				RecvThread.Join();
				RecvThread = null;
			}
			
			//	stop thread looping
			this.IsRunning = false;
		}
		
		void RecvLoop()
		{
			//	alloc once
			var Buffer = new byte[1033*2];
			while ( IsRunning )
			{
				//	throttle for now
				//	gr: if this is non blocking, THEN sleep if we get zero/wouldblock in recv
				//System.Threading.Thread.Sleep(10);
				var Flags = SocketFlags.None;
				
				if( false)
				{
					const int FIONREAD = 0x4004667F;
					byte[] outValue = System.BitConverter.GetBytes(0);
					Socket.IOControl(FIONREAD, null, outValue);
					uint bytesAvailable = System.BitConverter.ToUInt32(outValue, 0);
					//	gr: always 0
					Debug.Log("Data waiting " + Socket.Available + " IO:" + bytesAvailable);
				}
				var Result = Socket.Receive(Buffer, Flags);
				if (Result > 0)
				{
					//	todo: check if there is a common buffer size (ie, max mtu) and avoid alloc in subarray
					var Packet = Buffer.SubArray(0, Result);
					OnRecvPacket(Packet);
				}
				else
				{
					//	error
					//	todo: check for EWOULDBLOCK for nonblocking sockets
					Debug.LogError("Socket recv " + Result);
					return;
				}
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

	public UnityEvent_FrameCountPerSecondString OnKbpsString;
	public UnityEvent_FrameCountPerSecondFloat OnKbpsFloat;


	[Range(1, 9000)]
	public int MaxJobsPerFrame = 100;
	[Range(1, 9000)]
	public int MaxPacketsPerFrame = 100;
	

	public bool UseAsyncServer = true;	//	gr: use async by default, the Threaded one blocks with a lock too often and stalls the thread atm
	PopX.UdpServerSocket_Base Socket;
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

			/*
			System.Action<byte[]> OnPacketReady = (Packet) =>
			{				
				QueueJob(() =>
				{
					OnBinaryMessage(Packet);
				});
			};
			*/
			System.Action OnPacketReady = null;

			System.Action<float> OnKbpsReport = (Kbps) =>
			{
				QueueJob(() =>
							{
								this.OnKbpsReport(Kbps);
							});
			};

			SocketConnecting = true;
			if (UseAsyncServer)
				Socket = new PopX.UdpServerSocket_Async(Port, OnPacketReady, OnOpen, OnClose, OnKbpsReport);
			else
				Socket = new PopX.UdpServerSocket_Threaded(Port, OnPacketReady, OnOpen, OnClose, OnKbpsReport);
		}
		catch (System.Exception e) 
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
				Debug.LogWarning("Executed " + JobsExecutedCount + " this frame, " + JobQueue.Count + " jobs remaining",this);
			}

			//	pop packets
			if ( Socket!=null )
			{
				for ( var p=0;	p<MaxPacketsPerFrame;	p++)
				{
					var Packet = Socket.PopPacket();
					if (Packet == null)
						break;
					OnBinaryMessage(Packet);
				}
				var PendingPackets = Socket.GetPendingPackets();
				if (PendingPackets > 0 && VerboseDebug)
				{
					Debug.LogWarning(PendingPackets + " packets remaining", this);
				}
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

	public void Send(byte[] Data, System.Action<bool> OnDataSent = null)
	{
		//	todo: queue packets
		if (Socket == null)
			throw new System.Exception("Not connected");

		Socket.Send(Data, OnDataSent);
	}

	void OnKbpsReport(float Kpbs)
	{
		OnKbpsFloat.Invoke(Kpbs);
		OnKbpsString.Invoke(Kpbs.ToString("0.00 kb/s"));
	}
}
