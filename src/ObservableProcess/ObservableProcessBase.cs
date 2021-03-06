﻿using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elastic.ProcessManagement.Std;

namespace Elastic.ProcessManagement
{
	public abstract class ObservableProcessBase<TConsoleOut> : IObservableProcess<TConsoleOut>
		where TConsoleOut : ConsoleOut
	{
		private readonly ObservableProcessArguments _arguments;
		private readonly ManualResetEvent _completedHandle = new ManualResetEvent(false);

		protected ObservableProcessBase(string binary, params string[] arguments)
			: this(new ObservableProcessArguments(binary, arguments))
		{
		}

		protected ObservableProcessBase(ObservableProcessArguments arguments)
		{
			this._arguments = arguments;
			this.Binary = this._arguments.Binary;
			this.Process = CreateProcess();
			this.Start();
		}

		public virtual IDisposable Subscribe(IObserver<TConsoleOut> observer) => this.OutStream.Subscribe(observer);

		public IDisposable Subscribe(IConsoleOutWriter writer) => this.OutStream.Subscribe(writer.Write, writer.Write, delegate { });

		private readonly object _lock = new object();

		protected Process Process { get; }
		protected bool Started { get; set; }

		public string Binary { get; }

		public TimeSpan? WaitForExit
		{
			get => this._arguments?.WaitForExit;
			set => this._arguments.WaitForExit = value;
		}

		public int? ExitCode { get; private set; }

		public int? ProcessId => this.Process?.Id;

		protected IObservable<TConsoleOut> OutStream { get; private set; } = Observable.Empty<TConsoleOut>();

		private void Start()
		{
			if (this._isDisposed) throw new ObjectDisposedException(nameof(ObservableProcessBase<TConsoleOut>));
			if (this.Started) return;
			lock (_lock)
			{
				if (this.Started) return;
				this._completedHandle.Reset();
				this.OutStream = CreateConsoleOutObservable();
			}
		}

		protected abstract IObservable<TConsoleOut> CreateConsoleOutObservable();

		protected bool StartProcess(IObserver<TConsoleOut> observer)
		{
			try
			{
				if (this.Process.Start()) return true;

				OnError(observer, new CleanExitException($"Failed to start observable process: {this.Binary}"));
				this._completedHandle.Set();
				return false;
			}
			catch (Exception e)
			{
				OnError(observer, new CleanExitException($"Exception while starting observable process: {this.Binary}", e.Message, e));
				this._completedHandle.Set();
			}
			return false;
		}


		protected virtual void OnError(IObserver<TConsoleOut> observer, Exception e) => observer.OnError(e);
		protected virtual void OnCompleted(IObserver<TConsoleOut> observer) => observer.OnCompleted();

		private readonly object _exitLock = new object();

		protected void OnExit(IObserver<TConsoleOut> observer)
		{
			if (!this.Started || this.ExitCode.HasValue) return;
			lock (_exitLock)
			{
				if (!this.Started || this.ExitCode.HasValue) return;
				try
				{
					var c = this.Process.ExitCode;
					this.ExitCode = c;
				}
				finally
				{
					this.Stop();
					OnCompleted(observer);
				}
			}
		}

		private Process CreateProcess()
		{
			var args = this._arguments.Args;
			var processStartInfo = new ProcessStartInfo
			{
				FileName = this._arguments.Binary,
				Arguments = args != null ? string.Join(" ", args) : string.Empty,
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = false
			};
			this._arguments.AlterProcessStartInfo?.Invoke(processStartInfo);

			var p = new Process
			{
				EnableRaisingEvents = true,
				StartInfo = processStartInfo
			};
			return p;
		}

		/// <summary>
		/// Block until the process completes.
		/// </summary>
		/// <param name="timeout">The maximum time span we are willing to wait</param>
		/// <exception cref="CleanExitException">an exception that indicates a problem early in the pipeline</exception>
		public bool WaitForCompletion(TimeSpan timeout)
		{
			if (this._completedHandle.WaitOne(timeout))
			{
				return true;
			}
			this.Stop();
			return false;
		}

		private void Stop()
		{
			try
			{
				if (this.Process == null) return;

				var wait = this._arguments.WaitForExit;

				try
				{
					if (this.Started && wait.HasValue)
					{
						this.Process?.WaitForExit((int) wait.Value.TotalMilliseconds);
						if (!this.HardWaitForExit(TimeSpan.FromSeconds(1)))
						{
							this.Process?.Kill();
							this.Process?.WaitForExit();
						}
					}
					else if (this.Started)
					{
						this.Process?.Kill();
						//this.Process?.WaitForExit();
						//this.HardWaitForExit(wait ?? TimeSpan.FromSeconds(1));
					}
				}
				catch (InvalidOperationException)
				{
				}

				try
				{
					this.Process?.Dispose();
				}
				catch (NullReferenceException)
				{
					//the underlying call to .Close() can throw an NRE if you dispose too fast after starting
				}
			}
			finally
			{
				this.Started = false;
				//this.OutStream = null;
				this._completedHandle.Set();
			}
		}

		private bool HardWaitForExit(TimeSpan timeSpan)
		{
			var task = Task.Run(() => this.Process.WaitForExit());
			return (Task.WaitAny(task, Task.Delay(timeSpan)) == 0);
		}

		private bool _isDisposed = false;

		public void Dispose()
		{
			this.Stop();
			this._isDisposed = true;
		}
	}
}
