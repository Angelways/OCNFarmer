namespace OmenTools.OmenService;

public enum TooltipItemType : byte
{
    /// <summary>
    ///     物品名称
    /// </summary>
    Name = 0,

    /// <summary>
    ///     投影物品名称
    /// </summary>
    GlamourName = 1,

    /// <summary>
    ///     类别
    /// </summary>
    UICategory = 2,

    /// <summary>
    ///     物品主属性 (右侧第一)
    /// </summary>
    MainParam0Name = 4,

    /// <summary>
    ///     物品主属性 (右侧第二)
    /// </summary>
    MainParam1Name = 5,

    /// <summary>
    ///     物品主属性 (左侧第一)
    /// </summary>
    MainParam2Name = 6,

    /// <summary>
    ///     物品主属性值 (右侧第一)
    /// </summary>
    MainParam0Value = 7,

    /// <summary>
    ///     物品主属性值 (右侧第二)
    /// </summary>
    MainParam1Value = 8,

    /// <summary>
    ///     物品主属性值 (左侧第一)
    /// </summary>
    MainParam2Value = 9,

    /// <summary>
    ///     物品主属性额外偏移值 (右侧第一)
    /// </summary>
    MainParam0OffsetValue = 10,

    /// <summary>
    ///     物品主属性额外偏移值 (右侧第二)
    /// </summary>
    MainParam1OffsetValue = 11,

    /// <summary>
    ///     物品主属性额外偏移值 (左侧第一)
    /// </summary>
    MainParam2OffsetValue = 12,

    /// <summary>
    ///     物品描述
    /// </summary>
    Description = 13,

    /// <summary>
    ///     持有数量
    /// </summary>
    OwnedCount = 14,

    /// <summary>
    ///     “效果”分栏标题
    /// </summary>
    EffectTitle = 15,

    /// <summary>
    ///     效果
    /// </summary>
    Effect = 16,

    /// <summary>
    ///     “镶嵌魔晶石” 分栏标题
    /// </summary>
    AttachMateriaTitle = 17,

    /// <summary>
    ///     “可镶嵌的装备”
    /// </summary>
    AttachableGearCategory = 18,

    /// <summary>
    ///     “可镶嵌的装备” 的具体内容，物品品级之类的
    /// </summary>
    AttachableGearContent = 19,

    /// <summary>
    ///     所需职业类别
    /// </summary>
    ClassJobCategory = 22,

    /// <summary>
    ///     所需职业等级
    /// </summary>
    ClassJobLevel = 23,

    /// <summary>
    ///     收购价格 / 是否可在市场出售
    /// </summary>
    SellInfo = 25,

    /// <summary>
    ///     物品工匠名称
    /// </summary>
    MarkerName = 26,

    /// <summary>
    ///     物品品级
    /// </summary>
    ItemLevel = 27,

    /// <summary>
    ///     耐久度
    /// </summary>
    DurabilityValue = 28,

    /// <summary>
    ///     “精炼度”
    /// </summary>
    SpiritbondCategory = 29,

    /// <summary>
    ///     精炼度
    /// </summary>
    SpiritbondValue = 30,

    /// <summary>
    ///     可修理职业与等级信息
    /// </summary>
    RepairInfo = 31,

    /// <summary>
    ///     修理材料信息
    /// </summary>
    RepairMaterial = 32,

    /// <summary>
    ///     简易修理花费
    /// </summary>
    QuickRepairCost = 33,

    /// <summary>
    ///     可镶嵌魔晶石的职业与等级信息
    /// </summary>
    AttachMateriaInfo = 34,

    /// <summary>
    ///     精制魔晶石、武具投影、道具分解
    /// </summary>
    GearAbilityInfo = 35,

    /// <summary>
    ///     “特殊”标题
    /// </summary>
    SpecialTitle = 36,


    /// <summary>
    ///     装备特殊属性
    /// </summary>
    SpecialParam0 = 37,

    /// <summary>
    ///     装备特殊属性
    /// </summary>
    SpecialParam1 = 38,

    /// <summary>
    ///     装备特殊属性
    /// </summary>
    SpecialParam2 = 39,

    /// <summary>
    ///     装备特殊属性
    /// </summary>
    SpecialParam3 = 40,

    /// <summary>
    ///     装备特殊属性
    /// </summary>
    SpecialParam4 = 41,

    /// <summary>
    ///     “魔晶石工艺” 标题（就是装备镶嵌的魔晶石）
    /// </summary>
    MateriaTitle = 52,

    /// <summary>
    ///     镶嵌的魔晶石
    /// </summary>
    AttachedMateria0 = 53,

    /// <summary>
    ///     镶嵌的魔晶石
    /// </summary>
    AttachedMateria1 = 54,

    /// <summary>
    ///     镶嵌的魔晶石
    /// </summary>
    AttachedMateria2 = 55,

    /// <summary>
    ///     镶嵌的魔晶石
    /// </summary>
    AttachedMateria3 = 56,

    /// <summary>
    ///     镶嵌的魔晶石
    /// </summary>
    AttachedMateria4 = 57,

    /// <summary>
    ///     镶嵌的魔晶石属性
    /// </summary>
    AttachedMateria0Param = 58,

    /// <summary>
    ///     镶嵌的魔晶石属性
    /// </summary>
    AttachedMateria1Param = 59,

    /// <summary>
    ///     镶嵌的魔晶石属性
    /// </summary>
    AttachedMateria2Param = 60,

    /// <summary>
    ///     镶嵌的魔晶石属性
    /// </summary>
    AttachedMateria3Param = 61,

    /// <summary>
    ///     镶嵌的魔晶石属性
    /// </summary>
    AttachedMateria4Param = 62,

    /// <summary>
    ///     商店贩售信息
    /// </summary>
    ShopInfo = 63,

    /// <summary>
    ///     控制指引信息
    /// </summary>
    ControlHelp = 64
}
