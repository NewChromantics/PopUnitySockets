using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using WebSocketSharp;

using UnityEngine.Events;


[System.Serializable]
public class UnityEvent_MessageBinary : UnityEvent <byte[]> {}

[System.Serializable]
public class UnityEvent_MessageText : UnityEvent<string> { }

[System.Serializable]
public class UnityEvent_Hostname : UnityEvent <string> {}

[System.Serializable]
public class UnityEvent_HostnameError : UnityEvent <string,string> {}


public class PopWebsocketClient : MonoBehaviour
{
	[Header("First argument of event is host:port. Second is error")]
	public UnityEvent_Hostname			OnConnecting;
	public UnityEvent_Hostname			OnConnected;
	[Header("Invoked to match failed initial connect")]
	public UnityEvent_HostnameError		OnDisconnected;

	public UnityEvent_MessageBinary		OnMessageBinary;
	public UnityEvent_MessageText		OnMessageText;

	//	move these jobs to a thread!
	[Range(1,5000)]
	public int							MaxJobsPerFrame = 1000;

	WebSocket	Socket;
	bool		SocketConnecting = false;

	public bool	VerboseDebug = false;


	public List<string> Hosts = new List<string>() { "localhost" };
	public List<int>	Ports = new List<int>() { 80 };
	int CurrentHostPortIndex = 0;
	public string 		CurrentHost	
	{	
		get
		{	
			try
			{
				var HostIndex = (CurrentHostPortIndex / Ports.Count) % Hosts.Count;
				var PortIndex = CurrentHostPortIndex % Ports.Count;
				return AddPortToHostname( Hosts[HostIndex], Ports[PortIndex] );
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


	string AddPortToHostname(string Hostname,int Port)
	{
		//	not very robust atm, improve when required
		if (Hostname.Contains (":"))
			return Hostname;
		else
			return Hostname + ":" + Port;
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
		CurrentHostPortIndex++;
		Debug_Log("Connecting to " + Host + "...");
		OnConnecting.Invoke (Host);

		//	any failure from here should have a corresponding fail
		try
		{
			var NewSocket = new WebSocket("ws://" + Host);
			SocketConnecting = true;
			//NewSocket.Log.Level = LogLevel.TRACE;

			NewSocket.OnOpen += (sender, e) => {
    	        QueueJob (() => {
					Debug.Log("Connected");
					SocketConnecting = false;
					Socket = NewSocket;
					OnConnected.Invoke(Host);
				});
			};

			NewSocket.OnError += (sender, e) => {
	            QueueJob (() => {
					OnError( Host, e.Message, true );
				});
			};

			NewSocket.OnClose += (sender, e) => {
				SocketConnecting = false;
				/*
				if ( LastConnectedHost != null ){
					QueueJob (() => {
						SetStatus("Disconnected from " + LastConnectedHost );
					});
				}
				*/
				OnError( Host, "Closed", true);
			};

			//	gr: does this need to be a queued job?
			//	gr: it does now, 2017 throws because of use of the events
			NewSocket.OnMessage += (sender, e) => {

				System.Action Handler = ()=>
				{
					if ( e.Type == Opcode.TEXT )
						OnTextMessage( e.Data );
					else if ( e.Type == Opcode.BINARY )
						OnBinaryMessage( e.RawData );
					else
						OnError( Host, "Unknown opcode " + e.Type, false );
				};
				QueueJob(Handler);
			};
		
			//	socket assigned upon success
    	    NewSocket.ConnectAsync ();
		}
		catch(System.Exception e) {
			SocketConnecting = false;
			if (Socket != null) {
				Debug.LogWarning ("Unexpected non-null socket");
				Socket = null;
			}
			OnDisconnected.Invoke (Host, e.Message);
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



	public void OnTextMessage(string Message)
	{
		if (VerboseDebug)
			Debug_Log ("Text message: " + Message.Substring (0, System.Math.Min(40, Message.Length)));
		OnMessageText.Invoke (Message);
	}

	public void OnBinaryMessage(byte[] Message)
	{
		if (VerboseDebug)
			Debug_Log ("Binary Message: " + Message.Length + " bytes");
		OnMessageBinary.Invoke (Message);
	}

	void OnError(string Host,string Message,bool Close)
	{
		//SetStatus("Error: " + Message );
		Debug_Log(Host + " error: " + Message );
		OnDisconnected.Invoke (Host, Message);

		if (Close) {
			if (Socket != null) {

				//	recurses if we came here from on close
				if ( Socket.IsAlive )
					Socket.Close ();
				Socket = null;
				SocketConnecting = false;
			}
		}
	}


	void OnApplicationQuit()
	{
		if (Socket != null)
		{
			//	if (Socket.IsAlive)
			Socket.Close ();
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
		if (Socket == null)
			throw new System.Exception ("Not connected");

		Socket.SendAsync (Data,OnDataSent);
	}

	public void Send(string Data,System.Action<bool> OnDataSent=null)
	{
		if (Socket == null)
			throw new System.Exception ("Not connected");

		Socket.SendAsync (Data,OnDataSent);
	}

	public void SendJson<T>(T Object,System.Action<bool> OnDataSent=null)
	{
		Send( JsonUtility.ToJson(Object,true), OnDataSent );
	}
}
