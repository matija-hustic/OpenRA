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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Render trait for actors that change sprites if neighbors with the same trait are present.")]
	class WithWallSpriteBodyInfo : WithSpriteBodyInfo, Requires<BuildingInfo>
	{
		public readonly string Type = "wall";

		public override object Create(ActorInitializer init) { return new WithWallSpriteBody(init, this); }

		public override IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, RenderSpritesInfo rs, string image, int facings, PaletteReference p)
		{
			var adjacent = 0;

			if (init.Contains<RuntimeNeighbourInit>())
			{
				var location = CPos.Zero;
				if (init.Contains<LocationInit>())
					location = init.Get<LocationInit, CPos>();

				var neighbours = init.Get<RuntimeNeighbourInit, Dictionary<CPos, string[]>>();
				foreach (var kv in neighbours)
				{
					var haveNeighbour = false;
					foreach (var n in kv.Value)
					{
						var rb = init.World.Map.Rules.Actors[n].TraitInfoOrDefault<WithWallSpriteBodyInfo>();
						if (rb != null && rb.Type == Type)
						{
							haveNeighbour = true;
							break;
						}
					}

					if (!haveNeighbour)
						continue;

					if (kv.Key == location + new CVec(0, -1))
						adjacent |= 1;
					else if (kv.Key == location + new CVec(+1, 0))
						adjacent |= 2;
					else if (kv.Key == location + new CVec(0, +1))
						adjacent |= 4;
					else if (kv.Key == location + new CVec(-1, 0))
						adjacent |= 8;
				}
			}

			var anim = new Animation(init.World, image, () => 0);
			anim.PlayFetchIndex(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), Sequence), () => adjacent);

			yield return new SpriteActorPreview(anim, WVec.Zero, 0, p, rs.Scale);
		}
	}

	class WithWallSpriteBody : WithSpriteBody, INotifyRemovedFromWorld, ITick
	{
		readonly WithWallSpriteBodyInfo wallInfo;
		int adjacent = 0;
		bool dirty = true;

		public WithWallSpriteBody(ActorInitializer init, WithWallSpriteBodyInfo info)
			: base(init, info, () => 0)
		{
			wallInfo = info;
		}

		public override void DamageStateChanged(Actor self, AttackInfo e)
		{
			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), () => adjacent);
		}

		public void Tick(Actor self)
		{
			if (!dirty)
				return;

			// Update connection to neighbours
			var adjacentActors = CVec.Directions.SelectMany(dir =>
				self.World.ActorMap.GetActorsAt(self.Location + dir));

			adjacent = 0;
			foreach (var a in adjacentActors)
			{
				var rb = a.TraitOrDefault<WithWallSpriteBody>();
				if (rb == null || rb.wallInfo.Type != wallInfo.Type)
					continue;

				var location = self.Location;
				var otherLocation = a.Location;

				if (otherLocation == location + new CVec(0, -1))
					adjacent |= 1;
				else if (otherLocation == location + new CVec(+1, 0))
					adjacent |= 2;
				else if (otherLocation == location + new CVec(0, +1))
					adjacent |= 4;
				else if (otherLocation == location + new CVec(-1, 0))
					adjacent |= 8;
			}

			dirty = false;
		}

		public override void BuildingComplete(Actor self)
		{
			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), () => adjacent);
			UpdateNeighbours(self);
		}

		static void UpdateNeighbours(Actor self)
		{
			var adjacentActors = CVec.Directions.SelectMany(dir =>
					self.World.ActorMap.GetActorsAt(self.Location + dir))
				.Select(a => a.TraitOrDefault<WithWallSpriteBody>())
				.Where(a => a != null);

			foreach (var rb in adjacentActors)
				rb.dirty = true;
		}

		public void RemovedFromWorld(Actor self)
		{
			UpdateNeighbours(self);
		}
	}

	public class RuntimeNeighbourInit : IActorInit<Dictionary<CPos, string[]>>, ISuppressInitExport
	{
		[FieldFromYamlKey] readonly Dictionary<CPos, string[]> value = null;
		public RuntimeNeighbourInit() { }
		public RuntimeNeighbourInit(Dictionary<CPos, string[]> init) { value = init; }
		public Dictionary<CPos, string[]> Value(World world) { return value; }
	}
}
