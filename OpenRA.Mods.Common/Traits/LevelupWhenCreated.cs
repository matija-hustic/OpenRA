#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Actors possessing this trait should define the GainsExperience trait beforehand. When the prerequisites are fulfilled, ",
		"this trait grants a level-up to newly spawned actors. If additionally the actor's owning player defines the CanUpgradeIcons ",
		"trait, the production queue icon renders with an overlay defined in that trait.")]
	public class LevelupWhenCreatedInfo : ITraitInfo
	{
		public readonly string[] Prerequisites = null;

		[Desc("Number of levels to give to the actor on creation.")]
		public readonly int InitialLevels = 1;

		public object Create(ActorInitializer init) { return new LevelupWhenCreated(init, this); }
	}

	public class LevelupWhenCreated : INotifyAddedToWorld
	{
		readonly LevelupWhenCreatedInfo info;

		public LevelupWhenCreated(ActorInitializer init, LevelupWhenCreatedInfo info)
		{
			this.info = info;
		}

		public void AddedToWorld(Actor self)
		{
			if (self.Owner.PlayerActor.Trait<TechTree>().HasPrerequisites(info.Prerequisites))
			{
				var ge = self.TraitOrDefault<GainsExperience>();
				if (ge != null && ge.CanGainLevel)
					ge.GiveLevels(info.InitialLevels, true);
			}
		}
	}
}
