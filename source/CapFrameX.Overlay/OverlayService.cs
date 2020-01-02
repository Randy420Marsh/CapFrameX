﻿using CapFrameX.Contracts.Configuration;
using CapFrameX.Contracts.Overlay;
using CapFrameX.Data;
using CapFrameX.Extensions;
using CapFrameX.Statistics;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace CapFrameX.Overlay
{
	public class OverlayService : RTSSCSharpWrapper, IOverlayService
	{
		private readonly IStatisticProvider _statisticProvider;
		private readonly IOverlayEntryProvider _overlayEntryProvider;
		private readonly IAppConfiguration _appConfiguration;

		private IDisposable _disposableHeartBeat;
		private IDisposable _disposableCaptureTimer;

		private List<string> _runHistory = new List<string>();
		private int _refreshPeriod;
		private int _numberOfRuns;

		public Subject<bool> IsOverlayActiveStream { get; }

		public string SecondMetric { get; set; }

		public string ThirdMetric { get; set; }

		public OverlayService(IStatisticProvider statisticProvider, 
			IOverlayEntryProvider overlayEntryProvider, IAppConfiguration appConfiguration) 
			: base()
		{
			_statisticProvider = statisticProvider;
			_overlayEntryProvider = overlayEntryProvider;
			_appConfiguration = appConfiguration;

			_refreshPeriod = _appConfiguration.OSDRefreshPeriod;
			_numberOfRuns = _appConfiguration.SelectedHistoryRuns;
			SecondMetric = _appConfiguration.SecondMetricOverlay;
			ThirdMetric = _appConfiguration.ThirdMetricOverlay;
			IsOverlayActiveStream = new Subject<bool>();

			SetOverlayEntries(overlayEntryProvider?.GetOverlayEntries());
			overlayEntryProvider.EntryUpdateStream.Subscribe(x => 
				SetOverlayEntries(overlayEntryProvider?.GetOverlayEntries()));

			_runHistory = Enumerable.Repeat("N/A", _numberOfRuns).ToList();
			SetRunHistory(_runHistory.ToArray());
		 }

		public void ShowOverlay()
		{
			_disposableHeartBeat = GetOverlayRefreshHeartBeat();
		}

		public void HideOverlay()
		{
			_disposableHeartBeat?.Dispose();
			ReleaseOSD();
		}

		public void UpdateRefreshRate(int milliSeconds)
		{
			_refreshPeriod = milliSeconds;
			_disposableHeartBeat?.Dispose();
			_disposableHeartBeat = GetOverlayRefreshHeartBeat();
		}

		public void StartCountdown(int seconds)
		{
			IObservable<long> obs = Extensions.ObservableExtensions.CountDown(seconds);
			SetShowCaptureTimer(true);
			obs.Subscribe(t =>
			{
				SetCaptureTimerValue((int)t);

				if (t == 0)
					SetShowCaptureTimer(false);
			});
		}

		public void StartCaptureTimer()
		{
			SetShowCaptureTimer(true);
			_disposableCaptureTimer = GetCaptureTimer();
		}

		public void StopCaptureTimer()
		{
			SetShowCaptureTimer(false);
			SetCaptureTimerValue(0);
			_disposableCaptureTimer?.Dispose();
		}

		public void SetCaptureTimerValue(int t)
		{
			var captureTimer = _overlayEntryProvider.GetOverlayEntry("CaptureTimer");
			captureTimer.Value = $"{t.ToString()} s";
			SetOverlayEntries(_overlayEntryProvider?.GetOverlayEntries());
		}

		public void SetCaptureServiceStatus(string status)
		{
			var captureStatus = _overlayEntryProvider.GetOverlayEntry("CaptureServiceStatus");
			captureStatus.Value = status;
			SetOverlayEntries(_overlayEntryProvider?.GetOverlayEntries());
		}

		public void SetShowRunHistory(bool showHistory)
		{
			var history = _overlayEntryProvider.GetOverlayEntry("RunHistory");
			history.ShowOnOverlay = showHistory;
			SetOverlayEntries(_overlayEntryProvider?.GetOverlayEntries());
		}

		public void ResetHistory()
		{
			_runHistory = Enumerable.Repeat("N/A", _numberOfRuns).ToList();
			SetRunHistory(_runHistory.ToArray());
		}

		public void AddRunToHistory(List<string> captureData)
		{
			var frametimes = captureData.Select(line => RecordDataProvider.GetFrameTimeFromDataLine(line)).ToList();
			var average = _statisticProvider.GetFpsMetricValue(frametimes, EMetric.Average);
			var secondMetricValue = _statisticProvider.GetFpsMetricValue(frametimes, SecondMetric.ConvertToEnum<EMetric>());
			var thrirdMetricValue = _statisticProvider.GetFpsMetricValue(frametimes, ThirdMetric.ConvertToEnum<EMetric>());
			string numberFormat = string.Format("F{0}", _appConfiguration.FpsValuesRoundingDigits);
			var cultureInfo = CultureInfo.InvariantCulture;

			string secondMetricString = 
				SecondMetric.ConvertToEnum<EMetric>() != EMetric.None ? 
				$"{SecondMetric.ConvertToEnum<EMetric>().GetShortDescription()}=" +
				$"{secondMetricValue.ToString(numberFormat, cultureInfo)} " +
				$"FPS | " : string.Empty;

			string thirdMetricString =
				ThirdMetric.ConvertToEnum<EMetric>() != EMetric.None ?
				$"{ThirdMetric.ConvertToEnum<EMetric>().GetShortDescription()}=" +
				$"{thrirdMetricValue.ToString(numberFormat, cultureInfo)} " +
				$"FPS" : string.Empty;

			var currentList = new List<string>() 
			{ 
				$"Avg={average.ToString(numberFormat, cultureInfo)} " +
				$"FPS | " + secondMetricString + thirdMetricString 
			};
			_runHistory = currentList.Concat(_runHistory).ToList();

			if (_runHistory.Count > _numberOfRuns)
				_runHistory.RemoveAt(_runHistory.Count - 1);

			SetRunHistory(_runHistory.ToArray());
		}

		public void UpdateOverlayEntries()
		{
			SetOverlayEntries(_overlayEntryProvider?.GetOverlayEntries());
		}

		public void UpdateNumberOfRuns(int numberOfRuns)
		{
			_numberOfRuns = numberOfRuns;
			_runHistory = Enumerable.Repeat("N/A", _numberOfRuns).ToList();
			SetRunHistory(_runHistory.ToArray());
		}

		public string GetRTSSFullPath()
		{
			string installPath = string.Empty;

			try
			{
				// SOFTWARE\WOW6432Node\Unwinder\RTSS
				using (RegistryKey key = Registry.LocalMachine.OpenSubKey("Software\\WOW6432Node\\Unwinder\\RTSS"))
				{
					if (key != null)
					{
						object o = key.GetValue("InstallPath");
						if (o != null)
						{
							installPath = o as string;  //"as" because it's REG_SZ...otherwise ToString() might be safe(r)
						}
					}
				}

				// SOFTWARE\Unwinder\RTSS
				if (string.IsNullOrWhiteSpace(installPath))
				{
					using (RegistryKey key = Registry.LocalMachine.OpenSubKey("Software\\Unwinder\\RTSS"))
					{
						if (key != null)
						{
							object o = key.GetValue("InstallPath");
							if (o != null)
							{
								installPath = o as string;  //"as" because it's REG_SZ...otherwise ToString() might be safe(r)
							}
						}
					}
				}
			}
			catch (Exception)
			{
				throw;
			}

			return installPath;
		}

		private void SetShowCaptureTimer(bool show)
		{
			var captureTimer = _overlayEntryProvider.GetOverlayEntry("CaptureTimer");
			captureTimer.ShowOnOverlay = show;
		}

		private void CheckRTSSRunningAndRefresh()
		{
			var processes = Process.GetProcessesByName("RTSS");

			if (!processes.Any())
			{
				try
				{
					Process proc = new Process();
					proc.StartInfo.FileName = Path.Combine(GetRTSSFullPath());
					proc.StartInfo.UseShellExecute = false;
					proc.StartInfo.Verb = "runas";
					proc.Start();
				}
				catch { }
			}

			Refresh();
		}

		private IDisposable GetOverlayRefreshHeartBeat()
		{
			return Observable
				.Timer(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(_refreshPeriod))
				.Subscribe(x => CheckRTSSRunningAndRefresh());
		}

		private IDisposable GetCaptureTimer()
		{
			return Observable
				.Timer(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1))
				.Subscribe(x => SetCaptureTimerValue((int)x));
		}
	}
}