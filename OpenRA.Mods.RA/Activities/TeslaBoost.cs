#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Activities;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.RA.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.RA.Activities
{
	public class TeslaBoost : Activity
	{
		readonly Actor target;

		readonly WRange range;

		readonly UpgradeManager um;
		readonly string[] upgrades;
		readonly int grantMultiple;

		readonly bool pursueTarget;

		readonly IFacing facing;

		readonly int intervalTicks;
		readonly Armament arm;
		readonly Target ttarget;

		bool upgraded = false;
		int ticks = 0;

		public TeslaBoost(Actor self, Actor target, bool pursueTarget)
		{
			this.target = target;

			var uti = self.Info.Traits.Get<GivesTeslaBoostInfo>();

			range = uti.Range;

			um = target.TraitOrDefault<UpgradeManager>();
			upgrades = uti.Upgrades;
			grantMultiple = uti.GrantMultiple;

			this.pursueTarget = pursueTarget;

			facing = self.TraitOrDefault<IFacing>();

			intervalTicks = uti.IntervalTicks;
			arm = self.Trait<AttackBase>().Armaments.First(a => a.Info.Name == uti.Armament);
			ttarget = Target.FromActor(target);
		}

		public override Activity Tick(Actor self)
		{
			// Should the target for the boost be destroyed, cancel the activity
			if (!target.IsInWorld)
				return NextActivity;

			// Should the activity be canceled or target moves out of range, cancel the activity
			if (IsCanceled || (self.CenterPosition - target.CenterPosition).LengthSquared > range.RangeSquared)
			{
				if (upgraded)
				{
					upgraded = false;
					if (um != null)
						foreach (var upgrade in upgrades)
							for (var u = 0; u < grantMultiple; u++)
								um.RevokeUpgrade(target, upgrade, self);
				}

				// In the second case, should the boosting actor should pursue, pursue the target
				if (!IsCanceled && pursueTarget)
				{
					self.QueueActivity(new Move(self, ttarget, range));
					self.QueueActivity(new TeslaBoost(self, target, pursueTarget));
				}

				return NextActivity;
			}

			// If the upgrades have not yet been granted, grant them
			if (!upgraded)
			{
				upgraded = true;
				if (um != null)
					foreach (var upgrade in upgrades)
						for (var u = 0; u < grantMultiple; u++)
							um.GrantUpgrade(target, upgrade, self);
			}

			// For aesthetics, always turn towards the target
			var desiredFacing = Util.GetFacing(target.CenterPosition - self.CenterPosition, 0);
			if ((facing == null || facing.Facing != desiredFacing) && !self.TraitsImplementing<IDisableMove>().Any(d => d.MoveDisabled(self)))
				facing.Facing = Util.TickFacing(facing.Facing, desiredFacing, facing.ROT);

			// Every intervalTicks ticks render a shot (spark) between the actor and the target
			if (ticks-- > 0)
				return this;

			ticks = intervalTicks;

			// Render the boosting shot
			var barrel = arm.Barrels[arm.Burst % arm.Barrels.Length];
			var muzzlePosition = self.CenterPosition + arm.MuzzleOffset(self, barrel);
			var legacyFacing = arm.MuzzleOrientation(self, barrel).Yaw.Angle / 4;
			var args = new ProjectileArgs
			{
				Weapon = arm.Weapon,
				Facing = legacyFacing,
				DamageModifiers = new int[] { 0 },
				InaccuracyModifiers = new int[] { 0 },
				Source = muzzlePosition,
				SourceActor = self,
				PassiveTarget = ttarget.CenterPosition,
				GuidedTarget = ttarget
			};

			if (args.Weapon.Projectile != null)
			{
				var projectile = args.Weapon.Projectile.Create(args);
				if (projectile != null)
					self.World.Add(projectile);
			}

			return this;
		}
	}
}
