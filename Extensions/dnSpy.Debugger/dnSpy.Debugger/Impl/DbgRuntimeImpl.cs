﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.Engine;
using dnSpy.Contracts.Debugger.Engine.CallStack;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Debugger.CallStack;
using dnSpy.Debugger.Steppers;

namespace dnSpy.Debugger.Impl {
	sealed class DbgRuntimeImpl : DbgRuntime {
		public override DbgProcess Process { get; }
		public override RuntimeId Id { get; }
		public override string Name { get; }
		public override ReadOnlyCollection<string> Tags { get; }

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgAppDomain>> AppDomainsChanged;
		public override DbgAppDomain[] AppDomains {
			get {
				lock (lockObj)
					return appDomains.ToArray();
			}
		}
		readonly List<DbgAppDomain> appDomains;

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgModule>> ModulesChanged;
		public override DbgModule[] Modules {
			get {
				lock (lockObj)
					return modules.ToArray();
			}
		}
		readonly List<DbgModule> modules;

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgThread>> ThreadsChanged;
		public override DbgThread[] Threads {
			get {
				lock (lockObj)
					return threads.ToArray();
			}
		}
		readonly List<DbgThreadImpl> threads;

		DbgDispatcher Dispatcher => Process.DbgManager.Dispatcher;
		internal DbgEngine Engine { get; }

		internal CurrentObject<DbgThreadImpl> CurrentThread => currentThread;

		readonly object lockObj;
		readonly DbgManagerImpl owner;
		CurrentObject<DbgThreadImpl> currentThread;

		public DbgRuntimeImpl(DbgManagerImpl owner, DbgProcess process, DbgEngine engine) {
			lockObj = new object();
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			Process = process ?? throw new ArgumentNullException(nameof(process));
			Engine = engine ?? throw new ArgumentNullException(nameof(engine));
			var info = engine.RuntimeInfo;
			Id = info.Id;
			Name = info.Name;
			Tags = info.Tags;
			appDomains = new List<DbgAppDomain>();
			modules = new List<DbgModule>();
			threads = new List<DbgThreadImpl>();
		}

		internal void SetCurrentThread_DbgThread(DbgThreadImpl thread) {
			owner.Dispatcher.VerifyAccess();
			currentThread = new CurrentObject<DbgThreadImpl>(thread, currentThread.Break);
		}

		internal DbgThread SetBreakThread(DbgThreadImpl thread, bool tryOldCurrentThread = false) {
			Dispatcher.VerifyAccess();
			DbgThreadImpl newCurrent, newBreak;
			lock (lockObj) {
				newBreak = GetThread_NoLock(thread);
				if (tryOldCurrentThread && currentThread.Current?.IsClosed == false)
					newCurrent = currentThread.Current;
				else
					newCurrent = newBreak;
			}
			Debug.Assert((newBreak != null) == (newCurrent != null));
			currentThread = new CurrentObject<DbgThreadImpl>(newCurrent, newBreak);
			return newCurrent;
		}

		DbgThreadImpl GetThread_NoLock(DbgThreadImpl thread) {
			if (thread?.IsClosed == false)
				return thread;
			return threads.FirstOrDefault(a => a.IsMain) ?? (threads.Count == 0 ? null : threads[0]);
		}

		internal void ClearBreakThread() {
			Dispatcher.VerifyAccess();
			currentThread = default(CurrentObject<DbgThreadImpl>);
		}

		internal void Add_DbgThread(DbgAppDomainImpl appDomain) {
			Dispatcher.VerifyAccess();
			lock (lockObj)
				appDomains.Add(appDomain);
			AppDomainsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgAppDomain>(appDomain, added: true));
		}

		internal void Remove_DbgThread(DbgAppDomainImpl appDomain, bool pause) {
			Dispatcher.VerifyAccess();
			List<DbgThread> threadsToRemove = null;
			List<DbgModule> modulesToRemove = null;
			lock (lockObj) {
				bool b = appDomains.Remove(appDomain);
				if (!b)
					return;
				for (int i = threads.Count - 1; i >= 0; i--) {
					var thread = threads[i];
					if (thread.AppDomain == appDomain) {
						if (threadsToRemove == null)
							threadsToRemove = new List<DbgThread>();
						threadsToRemove.Add(thread);
						threads.RemoveAt(i);
					}
				}
				for (int i = modules.Count - 1; i >= 0; i--) {
					var module = modules[i];
					if (module.AppDomain == appDomain) {
						if (modulesToRemove == null)
							modulesToRemove = new List<DbgModule>();
						modulesToRemove.Add(module);
						modules.RemoveAt(i);
					}
				}
			}
			if (threadsToRemove != null && threadsToRemove.Count != 0)
				ThreadsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgThread>(threadsToRemove, added: false));
			if (modulesToRemove != null && modulesToRemove.Count != 0)
				ModulesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModule>(modulesToRemove, added: false));
			owner.RemoveAppDomain_DbgThread(this, appDomain, pause);
			AppDomainsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgAppDomain>(appDomain, added: false));
			if (threadsToRemove != null) {
				foreach (var thread in threadsToRemove)
					thread.Close(Dispatcher);
			}
			if (modulesToRemove != null) {
				foreach (var module in modulesToRemove)
					module.Close(Dispatcher);
			}
			appDomain.Close(Dispatcher);
		}

		internal void Add_DbgThread(DbgModuleImpl module) {
			Dispatcher.VerifyAccess();
			lock (lockObj)
				modules.Add(module);
			ModulesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModule>(module, added: true));
		}

		internal void Remove_DbgThread(DbgModuleImpl module, bool pause) {
			Dispatcher.VerifyAccess();
			lock (lockObj) {
				bool b = modules.Remove(module);
				if (!b)
					return;
			}
			owner.RemoveModule_DbgThread(this, module, pause);
			ModulesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModule>(module, added: false));
			module.Close(Dispatcher);
		}

		internal void Add_DbgThread(DbgThreadImpl thread) {
			Dispatcher.VerifyAccess();
			lock (lockObj)
				threads.Add(thread);
			ThreadsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgThread>(thread, added: true));
		}

		internal void Remove_DbgThread(DbgThreadImpl thread, bool pause) {
			Dispatcher.VerifyAccess();
			lock (lockObj) {
				bool b = threads.Remove(thread);
				if (!b)
					return;
			}
			owner.RemoveThread_DbgThread(this, thread, pause);
			ThreadsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgThread>(thread, added: false));
			thread.Close(Dispatcher);
		}

		internal void Remove_DbgThread(DbgEngineBoundCodeBreakpointImpl[] breakpoints) {
			Dispatcher.VerifyAccess();
			owner.RemoveBoundCodeBreakpoints_DbgThread(this, breakpoints);
		}

		internal void Freeze(DbgThreadImpl thread) => Engine.Freeze(thread);
		internal void Thaw(DbgThreadImpl thread) => Engine.Thaw(thread);

		internal DbgStackWalker CreateStackWalker(DbgThreadImpl thread) {
			var stackWalker = owner.Dispatcher2.Invoke(() => CreateStackWalker_DbgThread(thread));
			if (stackWalker != null)
				return stackWalker;
			// Invoke() returns null if shutdown has started but we can't return null
			return new DbgStackWalkerImpl(thread, new NullDbgEngineStackWalker());
		}

		DbgStackWalker CreateStackWalker_DbgThread(DbgThreadImpl thread) {
			Dispatcher.VerifyAccess();
			DbgEngineStackWalker engineStackWalker;
			if (Engine.IsClosed)
				engineStackWalker = new NullDbgEngineStackWalker();
			else
				engineStackWalker = Engine.CreateStackWalker(thread);
			return new DbgStackWalkerImpl(thread, engineStackWalker);
		}

		sealed class NullDbgEngineStackWalker : DbgEngineStackWalker {
			public override DbgEngineStackFrame[] GetNextStackFrames(int maxFrames) => Array.Empty<DbgEngineStackFrame>();
			protected override void CloseCore() { }
		}

		internal DbgStepper CreateStepper(DbgThreadImpl thread) => new DbgStepperImpl(owner, thread, Engine.CreateStepper(thread));
		internal void SetIP(DbgThreadImpl thread, DbgCodeLocation location) => Engine.SetIP(thread, location);
		internal bool CanSetIP(DbgThreadImpl thread, DbgCodeLocation location) => Engine.CanSetIP(thread, location);

		protected override void CloseCore() {
			Dispatcher.VerifyAccess();
			DbgThread[] removedThreads;
			DbgModule[] removedModules;
			DbgAppDomain[] removedAppDomains;
			lock (lockObj) {
				removedThreads = threads.ToArray();
				removedModules = modules.ToArray();
				removedAppDomains = appDomains.ToArray();
				threads.Clear();
				modules.Clear();
				appDomains.Clear();
			}
			currentThread = default(CurrentObject<DbgThreadImpl>);
			if (removedThreads.Length != 0)
				ThreadsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgThread>(removedThreads, added: false));
			if (removedModules.Length != 0)
				ModulesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModule>(removedModules, added: false));
			if (removedAppDomains.Length != 0)
				AppDomainsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgAppDomain>(removedAppDomains, added: false));
			foreach (var thread in removedThreads)
				thread.Close(Dispatcher);
			foreach (var module in removedModules)
				module.Close(Dispatcher);
			foreach (var appDomain in removedAppDomains)
				appDomain.Close(Dispatcher);
		}
	}
}
