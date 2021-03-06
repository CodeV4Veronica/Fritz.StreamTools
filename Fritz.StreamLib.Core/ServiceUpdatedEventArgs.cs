﻿using System;

// REF: https://dev.mixer.com/reference/constellation/index.html

namespace Fritz.StreamLib.Core
{
	public class ServiceUpdatedEventArgs : EventArgs
	{

		public string ServiceName { get; set; }

		public uint ChannelId { get; set; }

		public int? NewFollowers { get; set; }

		public int? NewViewers { get; set; }

		public bool? IsOnline { get; set; }
	}

}
