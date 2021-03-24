using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using WebSocketSharp;

[System.Serializable]
public class UnityEvent_FrameCountPerSecondString : UnityEngine.Events.UnityEvent <string> {}

[System.Serializable]
public class UnityEvent_FrameCountPerSecondFloat : UnityEngine.Events.UnityEvent <float> {}


namespace PopX
{
	public class FrameCounter
	{
		//	we don't use Time.time as that can only be used on the main thread
		//	UtcNow is supposedly faster https://stackoverflow.com/a/1561894/355753
		//	and we just need a consistent time reference, not an "accurate" time
		//	stopwatch and UTCNow seem to be on par with cost (tested against 80,000 calls per sec)

		//float NowSecs { get { return (float)System.DateTime.Now.Subtract(InitTime).TotalSeconds; } }
		//float NowSecs { get { return (float)System.DateTime.UtcNow.Subtract(InitTime).TotalSeconds; } }	//	subtract() to get a timespan in order to get total
		float NowSecs { get { return Stopwatch.ElapsedMilliseconds / 1000.0f; } } 
		//float NowSecs { get { return (float)UnityEngine.Time.time; } }
		//float NowSecs { get { return (float)UnityEngine.Time.realtimeSinceStartup; } }

		//	initialisation variables for different clocks above
		System.Diagnostics.Stopwatch Stopwatch = System.Diagnostics.Stopwatch.StartNew();
		System.DateTime InitTime = System.DateTime.UtcNow;	//	this can be any datetime value, just needs to be fixed. Clock comparison is done via LastLapTime.
		
		float Counter = 0;
		float LastLapTime;
		float ReportEverySeconds;
		System.Action<float> OnSecondLapsed;


		public FrameCounter(System.Action<float> OnSecondLapsed, float ReportEverySeconds = 1)
		{
			this.OnSecondLapsed = OnSecondLapsed;
			this.ReportEverySeconds = ReportEverySeconds;
			this.LastLapTime = NowSecs;
		}

		public void Add(float Add)
		{
			Counter += Add;
			CheckLap();
		}

		public void Add(int Add=1)
		{
			Counter += Add;
			CheckLap();
		}

		public void CheckLap()
		{
			float TimeSinceLap = NowSecs - LastLapTime;
			if (TimeSinceLap < ReportEverySeconds)
			{
				//Debug.Log("TimeSinceLap=" + TimeSinceLap + " now=" + NowSecs );
				return;
			}

			//	extrapolate count
			float Count = Counter / TimeSinceLap;
			Count *= 1 / ReportEverySeconds;

			//	reset & report
			Counter = 0;
			LastLapTime = NowSecs;
			OnSecondLapsed(Count);
		}
	}
}
