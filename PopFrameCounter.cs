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
		//float NowSecs { get { return (float)System.DateTime.Now.Subtract(StartTime).TotalSeconds; } }
		float NowSecs { get { return (float)System.DateTime.UtcNow.Subtract(StartTime).TotalSeconds; } }

		float Counter = 0;
		float LastLapTime;
		float ReportEverySeconds;
		System.Action<float> OnSecondLapsed;

		System.DateTime StartTime = System.DateTime.UtcNow;

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
