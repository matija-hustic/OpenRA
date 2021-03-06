#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Drawing;
using System.Reflection;
using OpenRA;

[assembly: Platform(typeof(OpenRA.Platforms.Default.DeviceFactory))]

namespace OpenRA.Platforms.Default
{
	public class DeviceFactory : IDeviceFactory
	{
		public IGraphicsDevice CreateGraphics(Size size, WindowMode windowMode)
		{
			return new Sdl2GraphicsDevice(size, windowMode);
		}

		public ISoundEngine CreateSound()
		{
			return new OpenAlSoundEngine();
		}
	}
}
