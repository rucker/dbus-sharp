// Copyright 2006 Alp Toker <alp@atoker.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Collections.Generic;
using org.freedesktop.DBus;

namespace DBus
{
	public sealed class Bus : Connection
	{
		static readonly string DBusName = "org.freedesktop.DBus";
		static readonly ObjectPath DBusPath = new ObjectPath ("/org/freedesktop/DBus");

		static Dictionary<string,Bus> buses = new Dictionary<string,Bus> ();

		class StarterBusValue {
			internal static Bus bus;
			static StarterBusValue () {
				bus = Bus.Open (Address.Starter);
			}
		}
		class SystemBusValue {
			internal static Bus bus;
			static SystemBusValue () {
				bus = Address.StarterBusType == "system" ? Starter : (Address.System != null ? Bus.Open (Address.System) : null);
			}
		}
		class SessionBusValue {
			internal static Bus bus;
			static SessionBusValue () {
				bus = Address.StarterBusType == "session" ? Starter : (Address.Session != null ? Bus.Open (Address.Session) : null);
			}
		}

		public static Bus System
		{
			get {
				return SystemBusValue.bus;
			}
		}
		public static Bus Session
		{
			get {
				return SessionBusValue.bus;
			}
		}
		public static Bus Starter
		{
			get {
				return StarterBusValue.bus;
			}
		}

		IBus bus;
		string address;
		string uniqueName;

		public static new Bus Open (string address)
		{
			if (address == null)
				throw new ArgumentNullException ("address");

			Bus bus;
			if (buses.TryGetValue (address, out bus))
				return bus;

			bus = new Bus (address);
			buses[address] = bus;

			return bus;
		}

		public Bus (string address) : base (address)
		{
			this.bus = GetObject<IBus> (DBusName, DBusPath);
			this.address = address;
			Register ();
		}

		//should this be public?
		//as long as Bus subclasses Connection, having a Register with a completely different meaning is bad
		void Register ()
		{
			if (uniqueName != null)
				throw new Exception ("Bus already has a unique name");

			uniqueName = bus.Hello ();
		}

		protected override void CloseInternal ()
		{
			/* In case the bus was opened with static method
			 * Open, clear it from buses dictionary
			 */
			if (buses.ContainsKey (address))
				buses.Remove (address);
		}

		protected override bool CheckBusNameExists (string busName)
		{
			if (busName == DBusName)
				return true;
			return NameHasOwner (busName);
		}

		public ulong GetUnixUser (string name)
		{
			return bus.GetConnectionUnixUser (name);
		}

		public RequestNameReply RequestName (string name)
		{
			return RequestName (name, NameFlag.None);
		}

		public RequestNameReply RequestName (string name, NameFlag flags)
		{
			return bus.RequestName (name, flags);
		}

		public ReleaseNameReply ReleaseName (string name)
		{
			return bus.ReleaseName (name);
		}

		public bool NameHasOwner (string name)
		{
			return bus.NameHasOwner (name);
		}

		public StartReply StartServiceByName (string name)
		{
			return StartServiceByName (name, 0);
		}

		public StartReply StartServiceByName (string name, uint flags)
		{
			return bus.StartServiceByName (name, flags);
		}

		internal protected override void AddMatch (string rule)
		{
			bus.AddMatch (rule);
		}

		internal protected override void RemoveMatch (string rule)
		{
			bus.RemoveMatch (rule);
		}

		public string GetId ()
		{
			return bus.GetId ();
		}

		public string UniqueName
		{
			get {
				return uniqueName;
			} set {
				if (uniqueName != null)
					throw new Exception ("Unique name can only be set once");
				uniqueName = value;
			}
		}
	}
}

// Local Variables:
// tab-width: 4
// c-basic-offset: 4
// indent-tabs-mode: t
// End:
