export const RARITY = [['0', '普通'], ['1', '高级'], ['2', '稀有'], ['3', '神器'], ['4', '史诗'], ['5', '勇者'], ['6', '传说']];
export const rarityName = n => RARITY.find(r => +r[0] === +n)?.[1] ?? String(n);

export const JOBS = [
  ['swordman', '鬼剑士（男）'], ['fighter', '格斗家（女）'], ['gunner', '神枪手（男）'], ['mage', '魔法师（女）'],
  ['priest', '圣职者（男）'], ['at gunner', '神枪手（女）'], ['thief', '暗夜使者'], ['at fighter', '格斗家（男）'],
  ['at mage', '魔法师（男）'], ['demonic swordman', '黑暗武士'], ['creator mage', '缔造者'], ['at swordman', '鬼剑士（女）'],
  ['knight', '守护者'], ['demonic lancer', '魔枪士'], ['at priest', '圣职者（女）'], ['gun blader', '枪剑士'],
];

export const TAG_LABEL = {
  weapon: '武器', coat: '上衣', shoulder: '头肩', pants: '下装', shoes: '鞋', waist: '腰带', amulet: '项链', wrist: '手镯', ring: '戒指', earring: '耳环',
  support: '辅助装备', 'magic stone': '魔法石', 'support weapon': '副武器', 'title name': '称号', 'name tag': '名称装饰卡', flag: '勋章',
  talisman: '护石', charm: '符咒', creature: '宠物', 'artifact red': '红色宠物装备', 'artifact blue': '蓝色宠物装备', 'artifact green': '绿色宠物装备',
  'hat avatar': '帽子装扮', 'hair avatar': '头发装扮', 'face avatar': '脸部装扮', 'coat avatar': '上衣装扮', 'breast avatar': '胸部装扮',
  'waist avatar': '腰部装扮', 'pants avatar': '下装装扮', 'shoes avatar': '鞋装扮', 'skin avatar': '皮肤装扮', 'aurora avatar': '光环装扮', 'weapon avatar': '武器装扮',
  waste: '消耗品', 'unlimited waste': '无限消耗品', 'waste instant item': '即时药剂', material: '材料', 'material instant item': '即时材料',
  'material expert job': '副职业材料', quest: '任务品', 'quest receive': '任务接取道具', 'avatar emblem': '徽章', 'flag gem': '守护珠', rune: '符文',
  recipe: '设计图', dye: '染色剂', throw: '投掷物', set: '放置物', feed: '宠物饲料', 'creature expitem': '宠物经验道具', 'enchant waste': '附魔宝珠',
  'cera package': '点券礼包', 'usable cera package': '可使用点券礼包', 'cera booster': '福包', booster: '礼盒', 'booster selection': '自选礼盒', 'booster random': '随机福袋',
  contract: '契约', coupon: '优惠券', disguise: '变装', 'random disguise': '随机变装', 'teleport potion': '传送药剂', 'npc teleport potion': '瞬移药剂',
  'expert town potion': '城镇药剂', 'town and dungeon': '城镇与副本道具', 'unlimited town and dungeon': '无限城镇与副本道具', etc: '其他', 'unlimited etc': '无限其他',
  'only effect': '特效道具', 'equipment effect': '装备特效', 'global effect': '光环', 'scale effect': '异次元效果', 'motion usable': '动作道具',
  'gold kaleido': '品级调整箱', 'upgrade limit cube': '限定晶体', 'item upgrad buff usual upgrade': '强化秘笈', 'item upgrad buff amplify upgrade': '增幅秘笈',
  'avatar gift': '装扮兑换券', 'auto use special buff': '增益赠礼', 'auto enchant': '自动附魔', 'apc legacy': '人偶助手', 'couple room item': '情侣房间道具',
  'guild contents': '公会道具', 'monster tarot card': '塔罗牌', 'roulette buff': '幸运签', 'quiz item': '答题道具', 'random reward item': '随机礼花',
  virtual: '虚拟消耗品', stackable: '任务券', 'stackable legacy': '旧堆叠物', legacy: '旧物品', 'upgradable legacy': '可升级旧物品',
  'multi upgradable legacy': '多重封印', 'multi upgradable legacy bonus cera': '点券封印', 'random upgradable legacy': '随机魔盒',
  'random goblin upgradable legacy': '哥布林魔盒', 'random ss upgradable legacy': '三星魔盒',
};
export const tagLabel = tag => !tag ? '未标类型' : (TAG_LABEL[tag] || '未标类型');

export const ITEM_TREE = [
  ['物品栏', [['g:equip', '装备'], ['g:use', '消耗品'], ['g:mat', '材料'], ['g:prof', '副职业'], ['g:quest', '任务']]],
  ['装扮', [['g:avatar', '装扮'], ['g:emblem', '徽章']]],
  ['宠物', [['g:pet', '宠物'], ['g:peteq', '宠物装备'], ['g:petuse', '宠物消耗品']]],
  ['护石', [['g:talisman', '护石'], ['g:rune', '符文']]],
  ['勋章', [['g:medal', '勋章'], ['g:gem', '守护珠']]],
];

export const BAG_TREE = [
  ['物品栏', 'bag', [['equip', '装备'], ['use', '消耗品'], ['mat', '材料'], ['prof', '副职业'], ['quest', '任务'], ['quick', '快捷栏']]],
  ['装扮', 'avatar', [['avatar', '装扮'], ['emblem', '徽章']]],
  ['宠物', 'pet', [['pet', '宠物'], ['peteq', '宠物装备'], ['petuse', '宠物消耗品']]],
  ['护石', 'stone', [['talisman', '护石'], ['rune', '符文']]],
  ['勋章', 'medal', [['medal', '勋章'], ['gem', '守护珠']]],
  ['仓库', 'cargo', [['personal', '个人仓库'], ['account', '账号金库']]],
];

const LIST = { Main: 0, Avatar: 1, PersonalCargo: 2, Equipment: 3, Pet: 7, AccountCargo: 12, GuildMedal: 38 };
export function bagKey(item) {
  const list = typeof item.list === 'number' ? item.list : LIST[item.listName];
  const slot = item.slot;
  if (list === 2) return 'personal';
  if (list === 12) return 'account';
  if (list === 1) return 'avatar';
  if (list === 7) return slot <= 139 ? 'pet' : slot <= 188 ? 'peteq' : 'petuse';
  if (list === 38) return slot <= 48 ? 'medal' : 'gem';
  if (list === 3) {
    if (slot <= 11) return 'avatar';
    if (slot === 32) return 'medal';
    if (slot === 26 || slot === 33) return 'pet';
    if (slot >= 27 && slot <= 29) return 'peteq';
    if (slot === 34) return 'talisman';
    if (slot === 35) return 'rune';
    return 'equip';
  }
  if (list === 0) {
    if (slot >= 3 && slot <= 8) return 'quick';
    if (slot >= 289) return 'emblem';
    if (slot >= 233) return 'prof';
    if (slot >= 177) return 'quest';
    if (slot >= 121) return 'mat';
    if (slot >= 65) return 'use';
    if (slot >= 9) return 'equip';
  }
  return 'equip';
}
export const worn = item => item.list === 3 || item.listName === 'Equipment';
