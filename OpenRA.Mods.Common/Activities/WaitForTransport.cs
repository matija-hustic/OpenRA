#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class WaitForTransport : Activity
	{
		readonly ICallForTransport transportable;

		Activity inner;

		public WaitForTransport(Actor self, Activity innerActivity)
		{
			transportable = self.TraitOrDefault<ICallForTransport>();
			inner = innerActivity;
		}

		public override Activity Tick(Actor self)
		{
			if (inner == null)
			{
				if (transportable != null)
					transportable.MovementCancelled(self);

				return NextActivity;
			}

			inner = Util.RunActivity(self, inner);
			return this;
		}

		public override void Cancel(Actor self)
		{
			if (transportable != null)
				transportable.WantsTransport = false;

			if (inner != null)
				inner.Cancel(self);
		}
	}
}
