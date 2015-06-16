#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.RA.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.RA.Traits
{
	public class GivesTeslaBoostInfo : ITraitInfo
	{
		[Desc("The upgrades to grant.")]
		public readonly string[] Upgrades = { "tesla" };

		[Desc("Make this actor grant the upgrades multiple times.")]
		public readonly int GrantMultiple = 1;

		[Desc("The range in which boosting is possible.")]
		public readonly WRange Range = WRange.FromCells(2);

		[Desc("Armament to use for the boost zap rendering.")]
		public readonly string Armament = "primary";

		[Desc("The actors that can receive the boost.")]
		public readonly string Target = "TeslaBoost";

		[Desc("Cursor to use when targeting for forced boost.")]
		public readonly string Cursor = "ability";

		[Desc("Voice to use when issuing order for forced boost.")]
		public readonly string Voice = "Move";

		[Desc("Order priority to use when targeting for forced boost.")]
		public readonly int OrderPriority = 7;

		[Desc("Interval between tesla boost zaps.")]
		public readonly int IntervalTicks = 75;

		[Desc("What target diplomatic stances are affected.")]
		public readonly Stance ValidStances = Stance.Ally;

		[Desc("Should the actor boost nearby targets when idling?")]
		public readonly bool BoostOnIdle = true;

		[Desc("Should the actor, after being given an explicit order to boost, pursue targets that move away?")]
		public readonly bool PursueMovingTargets = true;

		public object Create(ActorInitializer init) { return new GivesTeslaBoost(init.Self, this); }
	}

	public class GivesTeslaBoost : INotifyBecomingIdle, INotifyAddedToWorld, INotifyRemovedFromWorld, ITick, IIssueOrder, IResolveOrder, IOrderVoice
	{
		readonly GivesTeslaBoostInfo info;
		readonly Actor self;
		readonly string orderName = "TeslaBoost";
		readonly System.Drawing.Color targetLineColor = System.Drawing.Color.BlueViolet;

		int proximityTrigger;
		WPos cachedPosition;

		public GivesTeslaBoost(Actor self, GivesTeslaBoostInfo info)
		{
			this.info = info;
			this.self = self;
		}

		public IEnumerable<IOrderTargeter> Orders
		{
			get
			{
				yield return new TargetTypeOrderTargeter(new[] { info.Target }, orderName, info.OrderPriority, info.Cursor, false, true) { ForceAttack = false };
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, Target target, bool queued)
		{
			if (order.OrderID == orderName)
				return new Order(order.OrderID, self, queued) { TargetActor = target.Actor };

			return null;
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != orderName)
				return;

			var target = order.TargetActor != self && order.TargetActor.IsInWorld ? order.TargetActor : null;
			self.SetTargetLine(Target.FromActor(target), targetLineColor);
			self.CancelActivity();

			if ((self.CenterPosition - target.CenterPosition).HorizontalLengthSquared > info.Range.RangeSquared)
				self.QueueActivity(new Move(self, Target.FromActor(target), info.Range));

			self.QueueActivity(new TeslaBoost(self, target, info.PursueMovingTargets));
		}

		public string VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == orderName ? info.Voice : null;
		}

		public void OnBecomingIdle(Actor self)
		{
			if (!info.BoostOnIdle)
				return;

			var range = new WVec(info.Range, info.Range, WRange.Zero);

			// When becoming idle, automatically look within a circle of radius range for the closest actor
			// with appropriate stance that is able to be targeted for tesla boost.
			var target = self.World.ActorMap.ActorsInBox(self.CenterPosition - range, self.CenterPosition + range)
				.Where(a =>
				{
					if (a == self || (self.CenterPosition - a.CenterPosition).HorizontalLengthSquared > info.Range.RangeSquared
						|| !info.ValidStances.HasFlag(self.Owner.Stances[a.Owner]))
						return false;

					var tar = a.TraitOrDefault<ITargetable>();
					return tar != null && tar.TargetTypes.Contains(info.Target);
				})
				.OrderBy(a => (a.CenterPosition - self.CenterPosition).Length)
				.FirstOrDefault();

			if (target == null)
				return;

			self.SetTargetLine(Target.FromActor(target), targetLineColor);
			self.QueueActivity(new TeslaBoost(self, target, false));
		}

		public void AddedToWorld(Actor self)
		{
			if (!info.BoostOnIdle)
				return;

			cachedPosition = self.CenterPosition;
			proximityTrigger = self.World.ActorMap.AddProximityTrigger(cachedPosition, info.Range, ActorEntered, null);
		}

		public void RemovedFromWorld(Actor self)
		{
			if (!info.BoostOnIdle)
				return;

			self.World.ActorMap.RemoveProximityTrigger(proximityTrigger);
		}

		public void Tick(Actor self)
		{
			if (!info.BoostOnIdle)
				return;

			if (self.CenterPosition == cachedPosition)
				return;

			cachedPosition = self.CenterPosition;
			self.World.ActorMap.UpdateProximityTrigger(proximityTrigger, cachedPosition, info.Range);
		}

		void ActorEntered(Actor a)
		{
			// When an actor with the appropriate stance that is able to be targeted for tesla boost
			// appears (is built) within the current actor's range while it is idle, start boosting it.
			if (!self.IsIdle)
				return;

			if (a.Disposed)
				return;

			if (a == self)
				return;

			var stance = self.Owner.Stances[a.Owner];
			if (!info.ValidStances.HasFlag(stance))
				return;

			var tar = a.TraitOrDefault<ITargetable>();
			if (tar == null || !tar.TargetTypes.Contains(info.Target))
				return;

			self.SetTargetLine(Target.FromActor(a), targetLineColor);
			self.QueueActivity(new TeslaBoost(self, a, false));
		}
	}
}
