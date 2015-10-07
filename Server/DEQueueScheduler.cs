using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DEQueueScheduler ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEQueueScheduler : DEThreadLoop, IDEServerQueue
	{
		#region -- class ActionItem -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private abstract class ActionItem : IComparable<ActionItem>
		{
			private readonly Action action;

			public ActionItem(int id, Action action)
			{
				this.Id = id;
				this.action = action;
			} // ctor

			public int CompareTo(ActionItem other)
			{
				var r = Boundary - other.Boundary;
				return r == 0 ? other.Id - Id : r;
			} // func CompareTo

			public virtual void Execute()
			{
				action();
			} // proc Execute

			public virtual bool IsDue() => unchecked(Boundary - Environment.TickCount) < 0;

			public int Id { get; }
			public Action Action => action;
			public abstract int Boundary { get; }
		} // class ActionItem

		#endregion

		#region -- class ExecuteItem ------------------------------------------------------

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

		#region -- class EventItem --------------------------------------------------------

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

		#region -- class IdleItem ---------------------------------------------------------

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

		private DEServer server;
		private int lastItemId = 0;
		private readonly LinkedList<ActionItem> actions = new LinkedList<ActionItem>();
		private ManualResetEventSlim actionEvent = new ManualResetEventSlim(false);

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEQueueScheduler(DEServer server)
			: base(server, "des_main", priority: ThreadPriority.BelowNormal)
		{
			this.server = server;
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				lock (actions)
					actions.Clear();
			}
			base.Dispose(disposing);
		} // proc Dispose

		#endregion

		#region -- ExecuteLoop ------------------------------------------------------------

		public void ExecuteEvent(DEServerEvent eventType)
		{
			lock(actions)
			{
				foreach (var action in actions.OfType<EventItem>().Where(c => c.EventType == eventType))
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

		private bool IsActionAlive(Action action, out IDisposable executeLock)
		{
			var configItem = action.Target as DEConfigItem;
			if (configItem == null || configItem == server)
			{
				executeLock = null;
				return true;
			}
			else
			{
				switch (configItem.State)
				{
					case DEConfigItemState.Initialized:
						executeLock = configItem.EnterReadLock();
						return true;
					case DEConfigItemState.Invalid:
					case DEConfigItemState.Disposed:
						executeLock = null;
						return false;
					default:
						executeLock = null;
						return true;
				}
			}
		} // func GetActionLock

		protected override void ExecuteLoop()
		{
			// execute tasks
			ExecuteScheduler(1000);

			// execute actions
			var timeout = Int32.MaxValue;
			while (true)
			{
				var action = GetNextAction(true);
				if (action == null)
				{
					action = GetNextAction(false);
					timeout = action == null || action is EventItem ? Int32.MaxValue : unchecked(action.Boundary - Environment.TickCount);
					if (timeout < 0)
						timeout = 0;
					//else
					//	FilledEvent.Reset();
					break;
				}
				else
				{
					// schedule the action, that sets the schedule in the same thread
					IDisposable actionLock;
					if (IsActionAlive(action.Action, out actionLock))
					{
						Factory.StartNew(action.Execute).ContinueWith(
							t =>
							{
								try
								{
									t.Wait();
								}
								finally
								{
									actionLock?.Dispose();
									// re-execute action
									if (action is IdleItem)
										InsertAction(action);
								}
							});
					}
					RemoveAction(action);
				}
			}

			if (timeout > 0)
			{
				WaitHandle.WaitAny(new WaitHandle[] {
					StoppingEvent.WaitHandle,
					FilledEvent.WaitHandle,
					actionEvent.WaitHandle
				}, timeout == Int32.MaxValue ? -1 : timeout);
			}
			Thread.Sleep(100); // todo: fix
		} // proc ExecuteLoop

		#endregion

		#region -- List -------------------------------------------------------------------

		private int GetNextId()
		{
			lock (actions)
				return ++lastItemId;
		} // func GetNextId

		private ActionItem GetNextAction(bool dueOnly)
		{
			lock (actions)
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

		private void InsertAction(ActionItem action)
		{
			lock (actions)
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
					actions.AddFirst(action);

				FilledEvent.Set();
			}
		} // proc InsertAction

		private void RemoveAction(ActionItem action)
		{
			lock (actions)
				actions.Remove(action);
		} // proc RemoveAction

		private void RemoveItems(Action action)
		{
			lock (actions)
			{
				var n = actions.First;
				while (n != null)
				{
					var c = n;
					n = c.Next;

					if (c.Value.Action == action)
						actions.Remove(c);
				}
			}
		} // proc RemoveItems

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

		#endregion
	} // class DEQueueScheduler

	#endregion
}
