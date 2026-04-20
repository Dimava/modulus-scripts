/**
 * Reference types for SimpleResearchAndChallengeMultipliers (C# / Unity game).
 * Not consumed by ScriptEngine; for editors, Bun scripts, or docs only.
 */

/** Opaque Unity ScriptableObject / engine types */
type ResourceDataSO = object;
type ObjectiveTargetCategorySO = object;
type ObjectiveTargetItem = object;
type ObjectiveManager = object;
type ChallengeRewardLabels = object;
type CurrencyPersistentSO = object;

declare namespace Modulus.Dimava.SimpleResearchAndChallengeMultipliers {
  /** Hot-reload script entry (C#: `[ScriptEntry] sealed class … : ScriptMod`) */
  interface ScriptEntry {
    readonly ResearchCostMultiplier: number;
    readonly ChallengeRewardMultiplier: number;
    /** Delivery-bot cumulative tier thresholds (× mult vs authored). */
    readonly BotRequirementMultiplier: number;
    /** Module-shape challenge tier thresholds (× mult vs authored). */
    readonly ShapeChallengeRequirementMultiplier: number;
  }

  /**
   * When depth &gt; 0, `ResourceCost.GetAllCosts` postfix scales amounts (tech-tree only).
   */
  namespace TechTreeShardCostScope {
    function Enter(): void;
    function Exit(): void;
    const IsActive: boolean;
  }

  /**
   * Harmony postfix on `ResourceCost.GetAllCosts` — mutates returned dictionary in place
   * only while `TechTreeShardCostScope.IsActive`.
   */
  namespace ResourceCostGetAllCostsPatch {
    function Postfix(__result: Map<ResourceDataSO, number>): void;
  }

  /**
   * Scope brackets (Prefix Enter + Finalizer Exit):
   * - `DataShardsValidator.CanBuy` / `Buy`
   * - `TechTreeNodeView.SetCost`
   * - `TechTreeSaveDataNode` ctor `(int, int, ResourceCost)`
   */
  type ScopePatchTarget =
    | "DataShardsValidator.CanBuy"
    | "DataShardsValidator.Buy"
    | "TechTreeNodeView.SetCost"
    | "TechTreeSaveDataNode..ctor(int,int,ResourceCost)";
}

/** Game: shard cost bag (C# `ResourceCost`) */
declare interface ResourceCost {
  GetAllCosts(): Map<ResourceDataSO, number>;
}

/** Game: tech node authored cost */
declare interface TechTreeNodeSO {
  readonly Cost: ResourceCost;
  readonly IsUnlocked: boolean;
}

/** Game: pays research from `node.Cost` */
declare interface DataShardsValidator {
  CanBuy(node: TechTreeNodeSO): boolean;
  Buy(node: TechTreeNodeSO): void;
}

/** Game: module-challenge payout is replaced here with currency × multiplier; XP is unchanged. */
declare interface ObjectiveManagerAwardObjectiveRewards {
  (
    objectiveTargetCategory: ObjectiveTargetCategorySO,
    xpEarnedSource: unknown,
    currentTier: number
  ): void;
}

/** `ObjectiveTargetItem` fields used for rewards (names per decompiled game) */
declare interface ObjectiveTargetItemRewards {
  readonly XpReward: number;
  readonly CurrencyReward: number;
  readonly CurrenyRewardResourceData: ResourceDataSO | null;
}

/** Category: module challenges use shape data, not `ResourceCost` */
declare interface ObjectiveTargetCategoryResource {
  readonly HasShapeData: boolean;
  readonly HasResourceData: boolean;
}

export {};
