#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TecWare.DE.Server
{
	#region -- class DEQueueScheduler ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEQueueScheduler : DEThreadBase, IDEServerQueue
	{
		#region -- class QueueItem ------------------------------------------------------

		private abstract class QueueItem : IComparable<QueueItem>
		{
			public QueueItem(int id)
			{
				this.Id = id;
			} // ctor
			
			public int CompareTo(QueueItem other)
			{
				int r;

				if (other.Boundary == Int32.MaxValue && Boundary == Int32.MaxValue)
					r = 0;
				else if (other.Boundary == Int32.MaxValue)
					r = -1;
				else if (Boundary == Int32.MaxValue)
					r = 1;
				else
					r = unchecked(Boundary - other.Boundary);

				return r == 0 ? other.Id - Id : r;
			} // func CompareTo

			public abstract void Execute();

			public virtual bool IsDue()
				=> unchecked(Boundary - Environment.TickCount) <= 0;

			public int Id { get; }
			public abstract int Boundary { get; }
		} // class ActionItem

		#endregion

		#region -- class TaskItem -------------------------------------------------------

		private sealed class TaskItem : QueueItem
		{
			private readonly QueueScheduler scheduler;
			private readonly Task task;
			private readonly int boundary;

			public TaskItem(int id, QueueScheduler scheduler, Task task)
				: base(id)
			{
				this.scheduler = scheduler;
				this.task = task;
				this.boundary = Environment.TickCount;
			} // ctor

			public override void Execute() 
				=> scheduler.ExecuteTaskEntry(task);

			public override bool IsDue()
				=> true;

			public override int Boundary => boundary;
			public Task Task => task;
		} // class TaskItem

		#endregion

		#region -- class SendOrPostItem -------------------------------------------------

		private sealed class SendOrPostItem : QueueItem
		{
			private readonly SendOrPostCallback callback;
			private readonly object state;
			private readonly ManualResetEventSlim waitHandle;
			private readonly int boundary;

			public SendOrPostItem(int id, SendOrPostCallback callback, object state, ManualResetEventSlim waitHandle)
				: base(id)
			{
				this.callback = callback;
				this.state = state;
				this.waitHandle = waitHandle;

				this.boundary = Environment.TickCount;
			} // ctor

			public override void Execute()
				=> throw new NotImplementedException();

			public override int Boundary => boundary;

			public SendOrPostCallback Callback => callback;
			public object State => state;
			public ManualResetEventSlim WaitHandle => waitHandle;
		} // class SendOrPostItem

		#endregion

		#region -- class ActionItem -----------------------------------------------------

		/// <summary></summary>
		private abstract class ActionItem : QueueItem
		{
			private readonly Action action;

			public ActionItem(int id, Action action)
				: base(id)
			{
				this.action = action ?? throw new ArgumentNullException(nameof(action));
			} // ctor

			public override string ToString()
				=> $"{GetType().Name}: {action.Method}";

			public int CompareTo(ActionItem other)
			{
				int r;

				if (other.Boundary == Int32.MaxValue && Boundary == Int32.MaxValue)
					r = 0;
				else if (other.Boundary == Int32.MaxValue)
					r = -1;
				else if (Boundary == Int32.MaxValue)
					r = 1;
				else
					r = unchecked(Boundary - other.Boundary);

				return r == 0 ? other.Id - Id : r;
			} // func CompareTo

			public override void Execute()
				=> action();

			public Action Action => action;
		} // class ActionItem

		#endregion

		#region -- class ExecuteItem ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ExecuteItem : ActionItem
		{
			private readonly int boundary;

			public ExecuteItem(int id, Action action, int timeEllapsed)
				: base(id, action)
			{
				this.boundary = unchecked(Environment.TickCount + timeEllapsed);
			} // ctor

			public override int Boundary => boundary;
		} // class ExecuteItem

		#endregion

		#region -- class EventItem ------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class EventItem : ActionItem
		{
			public EventItem(int id, Action action, DEServerEvent eventType)
				: base(id, action)
			{
				this.EventType = eventType;
			} // ctor

			public override bool IsDue() => false;
			public override int Boundary => Int32.MaxValue;

			public DEServerEvent EventType { get; }
		} // class EventItem

		#endregion

		#region -- class IdleItem -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class IdleItem : ActionItem
		{
			private int exceptionCount = 0;
			private readonly int timeBetween;
			private int nextBoundary;

			public IdleItem(int id, Action action, int timeBetween)
				: base(id, action)
			{
				this.timeBetween = Math.Max(100, timeBetween);
				this.nextBoundary = unchecked(Environment.TickCount + timeBetween);
			} // ctor

			public override void Execute()
			{
				try
				{
					base.Execute();

					exceptionCount = 0;
					nextBoundary = unchecked(Environment.TickCount + timeBetween);
				}
				catch // enlarge the next call, in case of an exception
				{
					if (exceptionCount < 1 << 20)
						exceptionCount++;
					nextBoundary = unchecked(Environment.TickCount + timeBetween * exceptionCount);
					throw;
				}
			} // proc Execute

			public override int Boundary => nextBoundary;
		} // class IdleItem

		#endregion

		#region -- class QueueScheduler -------------------------------------------------

		private sealed class QueueScheduler : TaskScheduler
		{
			private readonly DEQueueScheduler scheduler;

			public QueueScheduler(DEQueueScheduler scheduler)
			{
				this.scheduler = scheduler;
			} // ctor

			protected override IEnumerable<Task> GetScheduledTasks()
			{
				lock (scheduler.MessageLoopSync)
					return scheduler.GetQueuedItemsUnsafe<TaskItem>().Select(c => c.Task).ToArray();
			} // func GetScheduledTasks

			protected override bool TryDequeue(Task task) 
				=> scheduler.RemoveTask(task);

			protected override void QueueTask(Task task)
			{
				if ((task.CreationOptions & TaskCreationOptions.LongRunning) != 0)
				{
					Task.Run(() => task, scheduler.cancellationTokenSource.Token);
					return;
				}

				scheduler.InsertAction(new TaskItem(scheduler.GetNextId(), this, task));
			} // QueueTask

			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
			{
				lock(scheduler.MessageLoopSync)
				{
					if (taskWasPreviouslyQueued)
						scheduler.RemoveTask(task);

					QueueTask(task);
				}
				return false;
			} // func TryExecuteTaskInline

			internal void ExecuteTaskEntry(Task t)
				=> base.TryExecuteTask(t);

			public override int MaximumConcurrencyLevel => 1;
		} // class QueueScheduler

		#endregion

		private readonly DEServer server;
		private readonly TaskFactory factory;
		private readonly CancellationTokenSource cancellationTokenSource;
		private int lastItemId = 0;
		private readonly LinkedList<QueueItem> actions = new LinkedList<QueueItem>();

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEQueueScheduler(DEServer server)
			: base(server, "des_main")
		{
			this.server = server;
			this.cancellationTokenSource = new CancellationTokenSource();
			this.factory = new TaskFactory(cancellationTokenSource.Token, TaskCreationOptions.None, TaskContinuationOptions.ExecuteSynchronously, new QueueScheduler(this));
		} // ctor

		protected override void SetThreadParameter(Thread thread)
		{
			thread.SetApartmentState(ApartmentState.MTA);
			thread.IsBackground = false;
			thread.Priority = ThreadPriority.BelowNormal;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				cancellationTokenSource.Cancel();
				lock (actions)
					actions.Clear();
			}
			base.Dispose(disposing);
		} // proc Dispose

		#endregion

		#region -- ExecuteLoop ------------------------------------------------------------

		public void ExecuteEvent(DEServerEvent eventType)
		{
			lock (actions)
			{
				var eventActions = actions.OfType<EventItem>().Where(c => c.EventType == eventType).ToArray();
				foreach (var action in eventActions)
				{
					try
					{
						action.Execute();
					}
					catch (Exception e)
					{
						Log.Except(e);
					}
				}
			}
		} // proc Shutdown

		private bool IsActionAlive(Action action)
		{
			var configItem = action.Target as DEConfigItem;
			if (configItem == null)
				return true;
			else
			{
				switch (configItem.State)
				{
					case DEConfigItemState.Invalid:
					case DEConfigItemState.Disposed:
						return false;
					default:
						return true;
				}
			}
		} // func IsActionAlive

		private static void ExecuteCallback(object state)
			=> ((QueueItem)state).Execute();

		protected override bool TryDequeueTask(CancellationToken cancellationToken, out SendOrPostCallback d, out object state, out ManualResetEventSlim wait)
		{
			redo:
			// execute actions
			var item = GetNextAction(true);
			if (item == null)
			{
				lock (MessageLoopSync)
				{
					item = GetNextAction(false);
					var timeout = item == null || item is EventItem ? Int32.MaxValue : unchecked(item.Boundary - Environment.TickCount);
					if (timeout < Int32.MaxValue)
					{
						if (timeout > 0)
						{
							ResetMessageLoopUnsafe();
							Task.Delay(timeout).GetAwaiter().OnCompleted(PulseMessageLoop);
						}
						else
							PulseMessageLoop();
					}
					else
						ResetMessageLoopUnsafe();
				}

				d = null;
				state = null;
				wait = null;
				return false;
			}
			else
			{
				RemoveAction(item);
				if (item is ActionItem action && !IsActionAlive(action.Action))
				{
					goto redo;
				}
				else if (item is SendOrPostItem callback)
				{
					d = callback.Callback;
					state = callback.State;
					wait = callback.WaitHandle;
					return true;
				}
				else
				{
					d = ExecuteCallback;
					state = item;
					wait = null;
					return true;
				}
			}
		} // func TryDequeueTask

		#endregion

		#region -- List -------------------------------------------------------------------

		private int GetNextId()
		{
			lock (MessageLoopSync)
				return ++lastItemId;
		} // func GetNextId

		private QueueItem GetNextAction(bool dueOnly)
		{
			lock (MessageLoopSync)
			{
				var c = actions.First;
				if (c == null)
					return null;

				if (dueOnly)
				{
					if (c.Value.IsDue())
						return c.Value;
					else
						return null;
				}
				else
					return c.Value;
			}
		} // func GetNextAction

		protected override void EnqueueTask(SendOrPostCallback d, object state, ManualResetEventSlim waitHandle)
			=> InsertAction(new SendOrPostItem(GetNextId(), d, state, waitHandle));

		private void InsertAction(QueueItem action)
		{
			lock (MessageLoopSync)
			{
				var pos = actions.First;
				while (pos != null)
				{
					if (pos.Value.CompareTo(action) > 0)
						break;

					pos = pos.Next;
				}
				if (pos != null)
					actions.AddBefore(pos, action);
				else
					actions.AddLast(action);

				//Debug.Print("Insert Action: {0}", action);
				PulseMessageLoop(); // mark list is changed
			}
		} // proc InsertAction

		private IEnumerable<T> GetQueuedItemsUnsafe<T>()
			where T : QueueItem
		{
			var n = actions.First;
			while (n != null)
			{
				var c = n;
				n = c.Next;

				if (c.Value is T)
					yield return (T)c.Value;
			}
		} // func GetQueuedItemsUnsafe

		private void RemoveAction(QueueItem action)
		{
			lock (MessageLoopSync)
			{
				//Debug.Print("Remove Action: {0}", action);
				actions.Remove(action);
			}
		} // proc RemoveAction

		private void RemoveItems(Action action)
		{
			lock (MessageLoopSync)
			{
				foreach (var cur in GetQueuedItemsUnsafe<ActionItem>().Where(c => c.Action == action))
					actions.Remove(cur);
			}
		} // proc RemoveItems

		private bool RemoveTask(Task task)
		{
			lock (MessageLoopSync)
			{
				var cur = GetQueuedItemsUnsafe<TaskItem>().FirstOrDefault(c => c.Task == task);
				if (cur != null)
				{
					actions.Remove(cur);
					return true;
				}
				else
					return false;
			}
		} // proc RemoveTask

		void IDEServerQueue.RegisterIdle(Action action, int timebetween)
			=> InsertAction(new IdleItem(GetNextId(), action, timebetween));

		void IDEServerQueue.RegisterEvent(Action action, DEServerEvent eventType)
			=> InsertAction(new EventItem(GetNextId(), action, eventType));

		void IDEServerQueue.RegisterCommand(Action action, int timeEllapsed)
			=> InsertAction(new ExecuteItem(GetNextId(), action, timeEllapsed));

		void IDEServerQueue.CancelCommand(Action action)
			=> RemoveItems(action);

		bool IDEServerQueue.IsQueueRunning => IsRunning;
		bool IDEServerQueue.IsQueueRequired => ManagedThreadId == Thread.CurrentThread.ManagedThreadId;

		TaskFactory IDEServerQueue.Factory => factory;

		#endregion
	} // class DEQueueScheduler

#endregion
}
