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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Used to mark a place that needs to be in possession for StrategicVictoryConditions.")]
	public class StrategicPointInfo : TraitInfo<StrategicPoint> { }
	public class StrategicPoint { }

	[Desc("Allows King of the Hill (KotH) style gameplay.")]
	public class StrategicVictoryConditionsInfo : ITraitInfo, Requires<MissionObjectivesInfo>
	{
		[Desc("Amount of time (in game ticks) that the player has to hold all the strategic points.", "Defaults to 5 minutes.")]
		public readonly int TicksToHold = 25 * 60 * 5;

		[Desc("Should the timer reset when the player loses hold of a strategic point.")]
		public readonly bool ResetOnHoldLost = true;

		[Desc("Percentage of all strategic points the player has to hold to win.")]
		public readonly float RatioRequired = 0.5f;

		[Desc("Delay for the end game notification in milliseconds.")]
		public readonly int NotificationDelay = 1500;

		[Desc("Description of the objective")]
		[Translate] public readonly string Objective = "Hold all the strategic positions!";

		public object Create(ActorInitializer init) { return new StrategicVictoryConditions(init.Self, this); }
	}

	public class StrategicVictoryConditions : ITick, ISync, INotifyObjectivesUpdated
	{
		readonly StrategicVictoryConditionsInfo info;

		[Sync] public int TicksLeft;
		readonly Player player;
		readonly MissionObjectives mo;
		int objectiveID = -1;

		public StrategicVictoryConditions(Actor self, StrategicVictoryConditionsInfo svcInfo)
		{
			info = svcInfo;
			TicksLeft = info.TicksToHold;
			player = self.Owner;
			mo = self.Trait<MissionObjectives>();
		}

		public IEnumerable<Actor> AllPoints
		{
			get { return player.World.ActorsHavingTrait<StrategicPoint>(); }
		}

		public int Total { get { return AllPoints.Count(); } }
		int Owned { get { return AllPoints.Count(a => WorldUtils.AreMutualAllies(player, a.Owner)); } }

		public bool Holding { get { return Owned >= info.RatioRequired * Total; } }

		public void Tick(Actor self)
		{
			if (player.WinState != WinState.Undefined || player.NonCombatant) return;

			if (objectiveID < 0)
				objectiveID = mo.Add(player, info.Objective, ObjectiveType.Primary, true);

			if (!self.Owner.NonCombatant && self.Owner.HasNoRequiredUnits())
				mo.MarkFailed(self.Owner, objectiveID);

			var others = self.World.Players.Where(p => !p.NonCombatant
				&& !p.IsAlliedWith(self.Owner));

			if (others.All(p => p.WinState == WinState.Lost))
				mo.MarkCompleted(player, objectiveID);

			if (others.Any(p => p.WinState == WinState.Won))
				mo.MarkFailed(player, objectiveID);

			// See if any of the conditions are met to increase the count
			if (Total > 0)
			{
				if (Holding)
				{
					// Hah! We met ths critical owned condition
					if (--TicksLeft == 0)
						mo.MarkCompleted(player, objectiveID);
				}
				else if (TicksLeft != 0)
					if (info.ResetOnHoldLost)
						TicksLeft = info.TicksToHold; // Reset the time hold
			}
		}

		public void OnPlayerLost(Player player)
		{
			Game.Debug("{0} is defeated.", player.PlayerName);

			foreach (var a in player.World.Actors.Where(a => a.Owner == player))
				a.Kill(a);

			Game.RunAfterDelay(info.NotificationDelay, () =>
			{
				if (Game.IsCurrentWorld(player.World) && player == player.World.LocalPlayer)
					Game.Sound.PlayNotification(player.World.Map.Rules, player, "Speech", "Lose", player.Faction.InternalName);
			});
		}

		public void OnPlayerWon(Player player)
		{
			Game.Debug("{0} is victorious.", player.PlayerName);

			Game.RunAfterDelay(info.NotificationDelay, () =>
			{
				if (Game.IsCurrentWorld(player.World) && player == player.World.LocalPlayer)
					Game.Sound.PlayNotification(player.World.Map.Rules, player, "Speech", "Win", player.Faction.InternalName);
			});
		}

		public void OnObjectiveAdded(Player player, int id) { }
		public void OnObjectiveCompleted(Player player, int id) { }
		public void OnObjectiveFailed(Player player, int id) { }
	}
}
