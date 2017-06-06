// Copyright 2006 Alp Toker <alp@atoker.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

using DBus.Unix;
using DBus.Protocol;

namespace DBus.Transports
{
	class UnixNativeTransport : UnixTransport
	{
		internal UnixSocket socket;

		public override string AuthString ()
		{
			long uid = Mono.Unix.Native.Syscall.geteuid ();
			return uid.ToString ();
		}

		public override void Open (string path, bool @abstract)
		{
			if (String.IsNullOrEmpty (path))
				throw new ArgumentException ("path");

			if (@abstract)
				socket = OpenAbstractUnix (path);
			else
				socket = OpenUnix (path);

			//socket.Blocking = true;
			SocketHandle = (long)socket.Handle;
			//Stream = new UnixStream ((int)socket.Handle);
			Stream = new UnixStream (socket);
		}

		public static byte[] GetSockAddr (string path)
		{
			byte[] p = Encoding.Default.GetBytes (path);

			byte[] sa = new byte[2 + p.Length + 1];

			//we use BitConverter to stay endian-safe
			byte[] afData = BitConverter.GetBytes (UnixSocket.AF_UNIX);
			sa[0] = afData[0];
			sa[1] = afData[1];

			for (int i = 0 ; i != p.Length ; i++)
				sa[2 + i] = p[i];
			sa[2 + p.Length] = 0; //null suffix for domain socket addresses, see unix(7)

			return sa;
		}

		public static byte[] GetSockAddrAbstract (string path)
		{
			byte[] p = Encoding.Default.GetBytes (path);

			byte[] sa = new byte[2 + 1 + p.Length];

			//we use BitConverter to stay endian-safe
			byte[] afData = BitConverter.GetBytes (UnixSocket.AF_UNIX);
			sa[0] = afData[0];
			sa[1] = afData[1];

			sa[2] = 0; //null prefix for abstract domain socket addresses, see unix(7)
			for (int i = 0 ; i != p.Length ; i++)
				sa[3 + i] = p[i];

			return sa;
		}

		internal UnixSocket OpenUnix (string path)
		{
			byte[] sa = GetSockAddr (path);
			UnixSocket client = new UnixSocket ();
			client.Connect (sa);
			return client;
		}

		internal UnixSocket OpenAbstractUnix (string path)
		{
			byte[] sa = GetSockAddrAbstract (path);
			UnixSocket client = new UnixSocket ();
			client.Connect (sa);
			return client;
		}

		internal unsafe override int Read (byte[] buffer, int offset, int count, UnixFDArray fdArray)
		{
			if (!Connection.UnixFDSupported || fdArray == null)
				return base.Read (buffer, offset, count, fdArray);

			if (count < 0 || offset < 0 || count + offset < count || count + offset > buffer.Length)
				throw new ArgumentException ();
			
			fixed (byte* ptr = buffer) {
				IOVector iov = new IOVector ();
				iov.Base = ptr + offset;
				iov.Length = count;

				msghdr msg = new msghdr ();
				msg.msg_iov = &iov;
				msg.msg_iovlen = 1;

				byte[] cmsg = new byte[CMSG_LEN ((ulong)(sizeof (int) * UnixFDArray.MaxFDs))];
				fixed (byte* cmsgPtr = cmsg) {
					msg.msg_control = (IntPtr)(cmsgPtr);
					msg.msg_controllen = (uint)cmsg.Length;

					int read = socket.RecvMsg (&msg, 0);

					for (cmsghdr* hdr = CMSG_FIRSTHDR (&msg); hdr != null; hdr = CMSG_NXTHDR (&msg, hdr)) {
						if (hdr->cmsg_level != 1) //SOL_SOCKET
							continue;
						if (hdr->cmsg_type != 0x01) //SCM_RIGHTS
							continue;
						int* data = (int*)CMSG_DATA (hdr);
						int fdCount = (int)(((ulong)hdr->cmsg_len - CMSG_LEN (0)) / sizeof (int));
						for (int i = 0; i < fdCount; i++)
							fdArray.FDs.Add (new UnixFD (data[i]));
					}

					if ((msg.msg_flags & 0x08) != 0) // MSG_CTRUNC
						throw new Exception ("Control message truncated");
					
					return read;
				}
			}
		}

		readonly object writeLock = new object ();
		
		private void AssertValidBuffer (byte[] buffer, long offset, long length)
		{
			if (buffer == null)
				throw new ArgumentNullException ("buffer");
			if (offset < 0)
				throw new ArgumentOutOfRangeException ("offset", "< 0");
			if (length < 0)
				throw new ArgumentOutOfRangeException ("length", "< 0");
			if (offset > buffer.LongLength)
				throw new ArgumentException ("destination offset is beyond array size");
			if (offset > (buffer.LongLength - length))
				throw new ArgumentException ("would overrun buffer");
		}

		// Send the two buffers and the FDs using sendmsg(), don't handle short writes
		// length1 + length2 must not be 0
		public unsafe long SendmsgShort (byte[] buffer1, long offset1, long length1,
										 byte[] buffer2, long offset2, long length2,
										 DBus.Protocol.UnixFDArray fds)
		{
			//Console.WriteLine ("SendmsgShort (X, {0}, {1}, {2}, {3}, {4}, {5})", offset1, length1, buffer2 == null ? "-" : "Y", offset2, length2, fds == null ? "-" : "" + fds.FDs.Count);
			AssertValidBuffer (buffer1, offset1, length1);
			if (buffer2 == null) {
				if (length2 != 0)
					throw new ArgumentOutOfRangeException ("length2", "!= 0 while buffer2 == null");
				offset2 = 0;
			} else {
				AssertValidBuffer (buffer2, offset2, length2);
			}

			fixed (byte* ptr1 = buffer1, ptr2 = buffer2) {
				var iovecs = new IOVector[] {
					new IOVector {
						Base = (ptr1 + offset1),
						Length = (int) length1,
					},
					new IOVector {
						Base = (ptr2 + offset2),
						Length = (int) length2,
					},
				};
				/* Simulate short writes
				   if (iovecs[0].iov_len == 0) {
				   iovecs[1].iov_len = Math.Min (iovecs[1].iov_len, 5);
				   } else {
				   iovecs[0].iov_len = Math.Min (iovecs[0].iov_len, 5);
				   iovecs[1].iov_len = 0;
				   }
				*/
				byte[] cmsg = null;

				// Create copy of FDs to prevent the user from Dispose()ing the
				// FDs in another thread between writing the FDs into the cmsg
				// buffer and calling sendmsg()
				fixed (IOVector* iovecPtr = iovecs) {
				using (var fds2 = fds == null ? null : fds.Clone ()) {
					int fdCount = fds2 == null ? 0 : fds2.FDs.Count;
					if (fdCount != 0) {
						// Create one SCM_RIGHTS control message
						cmsg = new byte[CMSG_SPACE ((uint) fdCount * sizeof (int))];
					}
					fixed (byte* cmsgPtr = cmsg) {
					var msghdr = new msghdr {
						msg_iov = iovecPtr,
						msg_iovlen = length2 == 0 ? 1 : 2,
						msg_control = (IntPtr) cmsgPtr,
						msg_controllen = cmsg == null ? 0 : (uint) cmsg.Length,
					};
					if (fdCount != 0) {
						var hdr = new cmsghdr {
							cmsg_len = (UIntPtr) CMSG_LEN ((uint) fdCount * sizeof (int)),
							cmsg_level = 1, // SOL_SOCKET
							cmsg_type = 1, // SCM_RIGHTS
						};
						*((cmsghdr*) cmsgPtr) = hdr;
						var data = CMSG_DATA ((cmsghdr*) cmsgPtr);
						fixed (byte* ptr = cmsg) {
							for (int i = 0; i < fdCount; i++)
								((int*) data)[i] = fds2.FDs[i].Handle;
						}
					}
					long r = socket.SendMsg (&msghdr, 0);
					if (r == 0)
						throw new Exception ("sendmsg() returned 0");
					return r;
					}
				}
				}
			}
		}

		// Send the two buffers and the FDs using sendmsg(), handle short writes
		public unsafe void Sendmsg (byte[] buffer1, long offset1, long length1,
									byte[] buffer2, long offset2, long length2,
									DBus.Protocol.UnixFDArray fds)
		{
			//SendmsgShort (buffer1, offset1, length1, buffer2, offset2, length2, fds); return;
			long bytes_overall = (long) length1 + length2;
			long written = 0;
			while (written < bytes_overall) {
				if (written >= length1) {
					long written2 = written - length1;
					written += SendmsgShort (buffer2, offset2 + written2, length2 - written2, null, 0, 0, written == 0 ? fds : null);
				} else {
					written += SendmsgShort (buffer1, offset1 + written, length1 - written, buffer2, offset2, length2, written == 0 ? fds : null);
				}
			}
			if (written != bytes_overall)
				throw new Exception ("written != bytes_overall");
		}
	
		internal override unsafe void WriteMessage (Message msg)
		{
			if (msg.UnixFDArray == null || msg.UnixFDArray.FDs.Count == 0) {
				base.WriteMessage (msg);
				return;
			}
			if (!Connection.UnixFDSupported)
				throw new Exception ("Attempting to write Unix FDs to a connection which does not support them");

			lock (writeLock) {
				var ms = new MemoryStream ();
				msg.Header.GetHeaderDataToStream (ms);
				var header = ms.ToArray ();
				Sendmsg (header, 0, header.Length, msg.Body, 0, msg.Body == null ? 0 : msg.Body.Length, msg.UnixFDArray);
			}
		}
		
		internal override bool TransportSupportsUnixFD { get { return true; } }

		
		static unsafe cmsghdr* CMSG_FIRSTHDR (msghdr* mhdr)
		{
			return mhdr->msg_controllen >= sizeof (cmsghdr) ? (cmsghdr*)mhdr->msg_control : null;
		}
		static unsafe ulong CMSG_ALIGN (ulong len)
		{
			return (len + (ulong)IntPtr.Size - 1) / (ulong)IntPtr.Size * (ulong)IntPtr.Size;
		}
		static unsafe cmsghdr* CMSG_NXTHDR (msghdr* mhdr, cmsghdr* cmsg)
		{
			if ((long) cmsg->cmsg_len < sizeof (cmsghdr))
				return null;

			cmsg = (cmsghdr*)((byte*)cmsg + CMSG_ALIGN ((ulong) cmsg->cmsg_len));
			if ((byte*)(cmsg + 1) > (byte*)mhdr->msg_control + mhdr->msg_controllen
				|| (byte*)cmsg + CMSG_ALIGN ((ulong) cmsg->cmsg_len) > (byte*)mhdr->msg_control + mhdr->msg_controllen)
				return null;
			return cmsg;
		}
		static unsafe byte* CMSG_DATA (cmsghdr* cmsg)
		{
			return (byte*)(cmsg + 1);
		}
		static unsafe ulong CMSG_LEN (ulong len)
		{
			return CMSG_ALIGN ((ulong)sizeof (cmsghdr)) + len;
		}
		static unsafe ulong CMSG_SPACE (ulong len)
		{
			return CMSG_ALIGN (len) + CMSG_ALIGN ((ulong) sizeof (cmsghdr));
		}
	}
}

// Local Variables:
// tab-width: 4
// c-basic-offset: 4
// indent-tabs-mode: t
// End:
