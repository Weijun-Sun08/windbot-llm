using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WindBot.Game
{
    /// <summary>
    /// 让本地模型（小蓝）来选行动：把局面和可选行动整理成中文，问她选哪个编号。
    /// 模型不可用或回答无法解析时返回 null，调用方退回 WindBot 原本的 AI。
    /// </summary>
    public static class LLMBrain
    {
        private static string Endpoint { get { return CfgStr("endpoint", "http://127.0.0.1:11450/v1/chat/completions"); } }
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static Dictionary<string, object> _cfg;
        private static Dictionary<string, object> Cfg()
        {
            if (_cfg != null) return _cfg;
            _cfg = new Dictionary<string, object>();
            try
            {
                if (File.Exists("llm.json"))
                {
                    _cfg = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText("llm.json"));
                }
            }
            catch { }
            return _cfg;
        }

        private static string CfgStr(string key, string fallback)
        {
            try { object v; if (Cfg().TryGetValue(key, out v) && v != null && v.ToString().Length > 0) return v.ToString(); } catch { }
            return fallback;
        }

        public static MainPhaseAction ChooseIdle(GameAI ai, MainPhase main)
        {
            try
            {
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " enter\r\n"); } catch { }
                List<MainPhaseAction> actions = new List<MainPhaseAction>();
                List<string> labels = new List<string>();

                foreach (ClientCard card in main.SummonableCards)
                {
                    labels.Add("通常召唤 " + NameOf(card));
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Summon, card.ActionIndex));
                }
                foreach (ClientCard card in main.SpecialSummonableCards)
                {
                    labels.Add("特殊召唤 " + NameOf(card));
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SpSummon, card.ActionIndex));
                }
                for (int i = 0; i < main.ActivableCards.Count; i++)
                {
                    ClientCard card = main.ActivableCards[i];
                    labels.Add("发动 " + NameOf(card) + "（" + SpeedText(card) + "）");
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Activate, card.ActionActivateIndex[main.ActivableDescs[i]]));
                }
                foreach (ClientCard card in main.MonsterSetableCards)
                {
                    labels.Add("盖放怪兽 " + NameOf(card));
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SetMonster, card.ActionIndex));
                }
                foreach (ClientCard card in main.SpellSetableCards)
                {
                    labels.Add("盖放魔陷 " + NameOf(card));
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SetSpell, card.ActionIndex));
                }
                foreach (ClientCard card in main.ReposableCards)
                {
                    labels.Add("改变表示形式 " + NameOf(card));
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Repos, card.ActionIndex));
                }
                if (main.CanBattlePhase)
                {
                    labels.Add("进入战斗阶段");
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase));
                }
                if (main.CanEndPhase)
                {
                    labels.Add("结束回合");
                    actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase));
                }

                if (labels.Count == 0) return null;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("现在的局面：");
                sb.AppendLine(Describe(ai));
                sb.AppendLine("你可以做这些行动：");
                for (int i = 0; i < labels.Count; i++)
                    sb.AppendLine((i + 1) + ". " + labels[i]);
                sb.AppendLine("只回答一个数字编号，不要解释。");

                string answer = Ask("你叫小蓝，正在打游戏王。根据局面选最合适的一步，只回答编号。", sb.ToString(), 8, DeckKnowledge(ai));
                if (answer == null) { try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " ask=null\r\n"); } catch { } return null; }

                int pick;
                if (!int.TryParse(ExtractNumber(answer), out pick)) return null;
                if (pick < 1 || pick > actions.Count) return null;
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " pick=" + pick + " " + labels[pick-1] + "\r\n"); } catch { }
                return actions[pick - 1];
            }
            catch
            {
                return null;
            }
        }

        public static BattlePhaseAction ChooseBattle(GameAI ai, BattlePhase battle)
        {
            try
            {
                var acts = new List<BattlePhaseAction>();
                var labels = new List<string>();
                for (int i = 0; i < battle.ActivableCards.Count; i++)
                {
                    labels.Add("发动 " + NameOf(battle.ActivableCards[i]));
                    acts.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.Activate, battle.ActivableCards[i].ActionIndex));
                }
                foreach (ClientCard card in battle.AttackableCards)
                {
                    labels.Add("用 " + NameOf(card) + " 攻击");
                    acts.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.Attack, card.ActionIndex));
                }
                if (battle.CanMainPhaseTwo) { labels.Add("进入主要阶段2"); acts.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo)); }
                if (battle.CanEndPhase) { labels.Add("结束回合"); acts.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase)); }
                if (labels.Count == 0) return null;
                int pick = Pick(ai, "战斗阶段可以做什么：", labels);
                return pick < 0 ? null : acts[pick];
            }
            catch { return null; }
        }

        private const int AshId = 14558127;

        public static int ChooseChain(GameAI ai, IList<ClientCard> cards, IList<int> descs, IList<bool> forces, int timing)
        {
            try
            {
                if (cards.Count == 0) return int.MinValue;
                var labels = new List<string>();
                var idxMap = new List<int>();
                int lastOwner = -1;
                try { if (ai.Duel.CurrentChain != null && ai.Duel.CurrentChain.Count > 0) lastOwner = ai.Duel.CurrentChain[ai.Duel.CurrentChain.Count - 1].Controller; } catch { }
                for (int i = 0; i < cards.Count; i++)
                {
                    labels.Add("连锁 " + NameOf(cards[i]) + "（" + (cards[i].HasType(YGOSharp.OCGWrapper.Enums.CardType.Monster) ? "诱发" : SpeedText(cards[i])) + "）");
                    idxMap.Add(i);
                }
                labels.Add("不连锁");
                string head = "现在可以连锁（" + TimingText(ai, timing) + "）：";
                // 连锁最后一张是自己发的时候，手坑不能连锁自己（2026-11-13）
                if (lastOwner == ai.Duel.Player) head = "当前连锁是你自己的效果：灰流丽/幽鬼兔/屋敷童这类手坑不要连锁自己，直接选「不连锁」；自己的二速效果可以继续。\n" + head;
                if (labels.Count >= 3) head = "同一时点诱发窗口：这些效果都是这个时点诱发的，能发就发（一般赚；自己的手坑不要连锁自己）；标了「二速」的看情况。\n" + head;
                int pick = Pick(ai, head, labels, true);
                if (pick < 0) return int.MinValue;
                if (pick == labels.Count - 1) return -1;
                return pick;
            }
            catch { return int.MinValue; }
        }

        public static bool? ChooseYesNo(GameAI ai, string question)
        {
            try
            {
                int pick = Pick(ai, question, new List<string> { "是", "否" }, true);
                if (pick == 0) return true;
                if (pick == 1) return false;
                return null;
            }
            catch { return null; }
        }

        public static IList<ClientCard> ChooseCards(GameAI ai, IList<ClientCard> cards, int min, int max, int hint, bool cancelable)
        {
            try
            {
                if (cards.Count == 0) return null;
                if (min <= 0) return null;
                var labels = new List<string>();
                foreach (ClientCard card in cards) labels.Add(NameOf(card));
                if (cancelable) labels.Add("取消");
                StringBuilder sb = new StringBuilder();
                sb.Append("需要选择 " + min + " 张卡：");
                for (int i = 0; i < labels.Count; i++) sb.Append((i + 1) + "." + labels[i] + " ");
                sb.Append("只回答编号，多张用逗号隔开。");
                string answer = Ask("你叫小蓝，正在打游戏王。根据局面选择卡片。", sb.ToString(), 12, DeckKnowledge(ai));
                if (answer == null) return null;
                var picked = new List<ClientCard>();
                foreach (string part in answer.Split(new[] { ',', '，', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int n;
                    if (!int.TryParse(part.Trim(), out n)) continue;
                    if (n >= 1 && n <= cards.Count && !picked.Contains(cards[n - 1])) picked.Add(cards[n - 1]);
                    if (picked.Count >= max) break;
                }
                if (picked.Count < min) return null;
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " cards=" + picked.Count + "\r\n"); } catch { }
                return picked;
            }
            catch { return null; }
        }

        private static int Pick(GameAI ai, string head, List<string> labels, bool allowPass = false)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Describe(ai));
            sb.AppendLine(head);
            for (int i = 0; i < labels.Count; i++) sb.AppendLine((i + 1) + ". " + labels[i]);
            sb.AppendLine("只回答一个编号。");
            string answer = Ask("你叫小蓝，正在打游戏王。根据局面选最合适的一步，只回答编号。", sb.ToString(), 8, DeckKnowledge(ai));
            if (answer == null) { try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " pick-null\r\n"); } catch { } return -1; }
            try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " raw=" + answer.Replace("\r","").Replace("\n","") + "\r\n"); } catch { }
            int n;
            if (!int.TryParse(ExtractNumber(answer), out n)) return -1;
            if (n < 1 || n > labels.Count) return -1;
            AddEvent("我选择：" + labels[n - 1]);
            return n - 1;
        }

        private static readonly List<string> _events = new List<string>();
        private static int _lastMyLp = -1;
        private static string _deckName = "";
        private static int _lastOpLp = -1;
        private const string LessonsFile = "lessons.md";

        public static void AddEvent(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                _events.Add(DateTime.Now.ToString("HH:mm") + " " + text);
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " event=" + text + "\r\n"); } catch { }
                if (_events.Count > 60) _events.RemoveAt(0);
            }
            catch { }
        }

        /// <summary>新的一局开始时：把上一局的经过交给模型复盘，写进 lessons.md</summary>
        /// <summary>一局结束：立刻复盘并写进 lessons.md</summary>
        public static void OnDuelEnd(bool win)
        {
            try
            {
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " reflect enter win=" + win + " events=" + _events.Count + "\r\n"); } catch { }
                if (_events.Count < 3) { _events.Clear(); return; }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(win ? "你赢了这一局。" : "你输掉了这一局。");
                if (!string.IsNullOrEmpty(_deckName)) sb.AppendLine("你用的卡组：" + _deckName + "。");
                sb.AppendLine("经过：");
                foreach (string e in _events) sb.AppendLine(e);
                sb.AppendLine("用两三句话复盘：哪里好、哪里错、下次怎么改；如果对手用的是和你一样的卡组（尤其是盈彩月夜），重点学他的展开顺序、时机和取舍。");
                string answer = Ask("你叫小蓝，在复盘刚打完的一局游戏王。", sb.ToString(), 160, "");
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " reflect answer=" + (answer == null ? "null" : "ok") + "\r\n"); } catch { }
                if (!string.IsNullOrEmpty(answer))
                    try { File.AppendAllText(LessonsFile, "\r\n## " + DateTime.Now.ToString("MM-dd HH:mm") + "\r\n" + answer.Trim() + "\r\n"); } catch { }
                _events.Clear();
                _lastMyLp = -1; _lastOpLp = -1;
            }
            catch { }
        }

        public static void OnDuelStart()
        {
            try
            {
                if (_events.Count < 3) { _events.Clear(); return; }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("这是你上一局的对战经过：");
                foreach (string e in _events) sb.AppendLine(e);
                if (_lastMyLp >= 0) sb.AppendLine("上一局最后：你的LP " + _lastMyLp + "，对方LP " + _lastOpLp + "（按这个判断输赢）。");
                sb.AppendLine("如果对手用的是和你一样的卡组，他这局的打法就是标准展开，重点学他的顺序、时机和取舍，写成你能照做的步骤。");
                sb.AppendLine("请用两三句话总结这一局：哪里打得好、哪里打错了、下次遇到同样情况怎么改。");
                string answer = Ask("你叫小蓝，在复盘自己刚打完的一局游戏王。", sb.ToString(), 160, "");
                if (!string.IsNullOrEmpty(answer))
                {
                    try { File.AppendAllText(LessonsFile, "\r\n## " + DateTime.Now.ToString("MM-dd HH:mm") + "\r\n" + answer.Trim() + "\r\n"); } catch { }
                }
                _events.Clear();
            }
            catch { }
        }

        private static string DeckKnowledge(GameAI ai)
        {
            try
            {
                string name = ai != null && ai.Game != null ? ai.Game.Deck : null;
                if (string.IsNullOrEmpty(name)) return "";
                string path = "decks/" + name + ".md";
                if (!File.Exists(path)) return "";
                string text = File.ReadAllText(path);
                return text.Length > 5000 ? text.Substring(0, 5000) : text;
            }
            catch { return ""; }
        }

        private static string Playbook()
        {
            try { return File.Exists("playbook.md") ? File.ReadAllText("playbook.md") : ""; } catch { return ""; }
        }

        private static string Lessons(int maxChars)
        {
            try
            {
                if (!File.Exists(LessonsFile)) return "";
                string all = File.ReadAllText(LessonsFile);
                if (all.Length <= maxChars) return all;
                return all.Substring(all.Length - maxChars);
            }
            catch { return ""; }
        }

        // 整条连锁：谁发动了什么、取什么对象，模型判断要不要跟连锁时要用（2026-11-09）
        private static string ChainText(GameAI ai)
        {
            try
            {
                if (ai == null || ai.Duel == null || ai.Duel.CurrentChainInfo == null || ai.Duel.CurrentChainInfo.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                int i = 0;
                foreach (ChainInfo ci in ai.Duel.CurrentChainInfo)
                {
                    i++;
                    string who = ci.ActivatePlayer == ai.Duel.Player ? "我" : "对方";
                    string name = ci.RelatedCard != null ? NameOf(ci.RelatedCard) : ("未知卡" );
                    sb.Append(i + "." + who + "「" + name + "」");
                    if (ci.Targets != null && ci.Targets.Count > 0) sb.Append("→目标：" + NamesOf(ci.Targets));
                    sb.Append("；");
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string PhaseText(GameAI ai)
        {
            try
            {
                if (ai == null || ai.Duel == null) return "?";
                switch (ai.Duel.Phase)
                {
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.Draw: return "抽卡阶段";
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.Standby: return "准备阶段";
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.Main1: return "主要阶段1";
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.Battle: return "战斗阶段";
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.Main2: return "主要阶段2";
                    case YGOSharp.OCGWrapper.Enums.DuelPhase.End: return "结束阶段";
                }
            }
            catch { }
            return ai.Duel.Phase.ToString();
        }

        private static string TimingText(GameAI ai, int timing)
        {
            string extra = "";
            try
            {
                if (ai != null && ai.Duel != null && ai.Duel.CurrentChain != null && ai.Duel.CurrentChain.Count > 0)
                    extra = "当前连锁：" + ChainText(ai) + "；";
                if (ai != null && ai.Duel != null) extra += "阶段：" + ai.Duel.Phase + "；";
            }
            catch { }
            string hint = "";
            try
            {
                if (timing > 0)
                {
                    YGOSharp.OCGWrapper.NamedCard c = YGOSharp.OCGWrapper.NamedCardsManager.GetCard(timing);
                    if (c != null) hint = "时点来自「" + c.Name + "」；";
                    else hint = "时点提示码 " + timing + "；";
                }
            }
            catch { }
            return hint + extra;
        }

        private static string DescOf(ClientCard card, int max)
        {
            try
            {
                if (card == null || card.Data == null || string.IsNullOrEmpty(card.Data.Description)) return "";
                string d = card.Data.Description.Replace("\r", " ").Replace("\n", " ");
                return d.Length > max ? d.Substring(0, max) + "…" : d;
            }
            catch { return ""; }
        }

        // 公开区域全量效果：每张截断，总长度封顶，超了优先保留前面的（场上优先）
        private static string EffectAll(IEnumerable<ClientCard> cards, int perCard, int totalMax)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ClientCard c in cards)
            {
                if (c == null) continue;
                string d = DescOf(c, perCard);
                if (d.Length == 0) continue;
                string piece = "【" + NameOf(c) + "】" + d + "；";
                if (sb.Length + piece.Length > totalMax) break;
                sb.Append(piece);
            }
            return sb.ToString();
        }

        private static string EffectList(IEnumerable<ClientCard> cards, int maxCards, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (ClientCard c in cards)
            {
                if (c == null) continue;
                sb.Append("【" + NameOf(c) + "】" + DescOf(c, maxChars) + "；");
                if (++n >= maxCards) break;
            }
            return sb.ToString();
        }

        private static string NamesOf(IEnumerable<ClientCard> cards)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ClientCard c in cards) if (c != null) sb.Append(NameOf(c) + "、");
            return sb.ToString();
        }

        private static string NameOf(ClientCard card)
        {
            try { return card.Name; } catch { return "卡片" + card.Id; }
        }

        private static string Describe(GameAI ai)
        {
            StringBuilder sb = new StringBuilder();
            try { if (ai.Game != null) _deckName = ai.Game.Deck; } catch { }
            int me = ai.Duel.Player;
            ClientField my = ai.Duel.Fields[me];
            ClientField op = ai.Duel.Fields[1 - me];

            if (_lastMyLp >= 0 && (_lastMyLp != my.LifePoints || _lastOpLp != op.LifePoints))
                AddEvent("LP变化：我 " + _lastMyLp + "→" + my.LifePoints + "，对方 " + _lastOpLp + "→" + op.LifePoints);
            _lastMyLp = my.LifePoints;
            _lastOpLp = op.LifePoints;

            string lessons = Lessons(600);
            if (lessons.Length > 0) sb.AppendLine("以往经验：" + lessons.Replace("\r", " ").Replace("\n", " "));
            if (_events.Count > 0)
                sb.AppendLine("本局经过：" + string.Join("；", _events.GetRange(Math.Max(0, _events.Count - 30), Math.Min(30, _events.Count)).ToArray()));

            sb.AppendLine("第 " + ai.Duel.Turn + " 回合，阶段：" + PhaseText(ai) + "，我是玩家 " + me + "。");
            sb.AppendLine("LP：我 " + my.LifePoints + " / 对方 " + op.LifePoints + "。");
            sb.AppendLine("手牌：" + NamesOf(my.Hand));
            sb.AppendLine("我方场上怪兽：" + ZoneText(my.MonsterZone, true));
            sb.AppendLine("我方魔陷区：" + ZoneText(my.SpellZone, true));
            sb.AppendLine("对方场上怪兽：" + ZoneText(op.MonsterZone, false));
            sb.AppendLine("对方魔陷区（数量）：" + CountSet(op.SpellZone));
            sb.AppendLine("我方墓地：" + NamesOf(my.Graveyard));
            sb.AppendLine("我方墓地效果：" + EffectAll(my.Graveyard, 50, 900));
            sb.AppendLine("对方墓地：" + NamesOf(op.Graveyard));
            sb.AppendLine("对方墓地效果：" + EffectAll(op.Graveyard, 50, 900));
            sb.AppendLine("我方除外：" + NamesOf(my.Banished) + "；对方除外：" + NamesOf(op.Banished));
            sb.AppendLine("我方除外效果：" + EffectAll(my.Banished, 45, 500));
            sb.AppendLine("对方除外效果：" + EffectAll(op.Banished, 45, 500));
            sb.AppendLine("卡组剩余：我 " + my.Deck.Count + " / 对方 " + op.Deck.Count + "；额外：我 " + my.ExtraDeck.Count + " / 对方 " + op.ExtraDeck.Count);
            sb.AppendLine("手牌效果：" + EffectAll(my.Hand, 55, 900));
            sb.AppendLine("我方场上效果：" + EffectAll(my.MonsterZone, 55, 700) + EffectAll(my.SpellZone, 55, 600));
            sb.AppendLine("对方场上效果：" + EffectAll(FaceupOnly(op.MonsterZone), 55, 700));
            try { string chain = ChainText(ai); if (chain.Length > 0) sb.AppendLine("当前连锁：" + chain); } catch { }
            sb.AppendLine("对方手牌 " + op.Hand.Count + " 张。");
            return sb.ToString();
        }

        // 场上怪兽的等级/属性/种族/种类，模型判断能不能融合/同调/超量时要用（2026-11-09）
        private static string MonsterInfo(ClientCard c)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Xyz)) sb.Append("R" + c.Rank);
                else if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Link)) sb.Append("L" + c.LinkCount);
                else sb.Append("Lv" + c.Level);
                sb.Append("/" + AttrName(c.Attribute) + "/" + RaceName(c.Race));
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Fusion)) sb.Append("/融合");
                else if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Synchro)) sb.Append("/同调");
                else if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Xyz)) sb.Append("/超量");
                else if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Link)) sb.Append("/连接");
                else if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Ritual)) sb.Append("/仪式");
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Tuner)) sb.Append("/调整");
                return sb.ToString();
            }
            catch { return ""; }
        }

        // 魔陷的种类：速攻/永续/装备/场地/反击/通常，模型判断能否响应时要用（2026-11-09）
        // 给模型一个粗略的速度提示：魔陷能看出速攻/反击/通常；怪兽只能标「怪兽效果」（2026-11-09）
        private static string SpeedText(ClientCard c)
        {
            try
            {
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Monster)) return "怪兽效果";
                string s = SpellInfo(c);
                if (s.Contains("反击")) return s + "·三速";
                if (s.Contains("速攻") || s.Contains("陷阱")) return s + "·二速";
                return s + "·一速";
            }
            catch { return ""; }
        }

        private static string SpellInfo(ClientCard c)
        {
            try
            {
                var T = typeof(YGOSharp.OCGWrapper.Enums.CardType);
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Counter)) return "反击陷阱";
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.QuickPlay)) return "速攻魔法";
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Continuous)) return c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Trap) ? "永续陷阱" : "永续魔法";
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Equip)) return "装备魔法";
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Field)) return "场地魔法";
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Ritual)) return "仪式魔法";
                return c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Trap) ? "通常陷阱" : "通常魔法";
            }
            catch { return ""; }
        }

        private static string AttrName(int a)
        {
            switch (a)
            {
                case 1: return "地"; case 2: return "水"; case 4: return "火"; case 8: return "风";
                case 16: return "光"; case 32: return "暗"; case 64: return "神"; default: return "?";
            }
        }

        private static string RaceName(int r)
        {
            switch (r)
            {
                case 1: return "战士"; case 2: return "魔法师"; case 4: return "天使"; case 8: return "恶魔";
                case 16: return "不死"; case 32: return "机械"; case 64: return "水"; case 128: return "炎";
                case 256: return "雷"; case 512: return "龙"; case 1024: return "兽"; case 2048: return "兽战士";
                case 4096: return "植物"; case 8192: return "昆虫"; case 16384: return "念动力"; case 32768: return "幻龙";
                case 65536: return "电子界"; case 131072: return "恐龙"; case 262144: return "海龙"; case 524288: return "岩石";
                case 1048576: return "鱼"; case 2097152: return "爬虫"; case 4194304: return "幻想魔"; default: return "?";
            }
        }

        private static string ZoneText(ClientCard[] zone, bool mine)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ClientCard c in zone)
            {
                if (c == null) continue;
                // 里侧表示、且不是自己的卡：正常玩家看不到卡名，只给「里侧」
                if (!mine && !c.IsFaceup())
                {
                    sb.Append("里侧卡、");
                    continue;
                }
                if (c.HasType(YGOSharp.OCGWrapper.Enums.CardType.Monster)) sb.Append(NameOf(c) + "(" + c.Attack + "/" + c.Defense + " " + PosText(c) + " " + MonsterInfo(c) + ")、");
                else sb.Append(NameOf(c) + "(" + PosText(c) + " " + SpellInfo(c) + ")、");
            }
            return sb.ToString();
        }

        private static int CountSet(ClientCard[] zone)
        {
            int n = 0;
            foreach (ClientCard c in zone) if (c != null) n++;
            return n;
        }

        private static string PosText(ClientCard c)
        {
            try
            {
                if (!c.IsFaceup()) return "里侧";
                if (c.IsAttack()) return "攻";
                return "守";
            }
            catch { return "?"; }
        }

        private static IEnumerable<ClientCard> FaceupOnly(ClientCard[] zone)
        {
            List<ClientCard> list = new List<ClientCard>();
            foreach (ClientCard c in zone) if (c != null && c.IsFaceup()) list.Add(c);
            return list;
        }

        private static string ExtractNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            return sb.ToString();
        }

        public static string Ask(string system, string user, int maxTokens)
        {
            return Ask(system, user, maxTokens, "");
        }

        public static string Ask(string system, string user, int maxTokens, string deckKnowledge)
        {
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = CfgStr("model", "local");
                payload["temperature"] = 0.7;
                payload["top_p"] = 0.9;
                payload["max_tokens"] = maxTokens;
                payload["stream"] = false;
                payload["messages"] = new object[]
                {
                    new Dictionary<string, object> { { "role", "system" }, { "content", system + "\n\n" + Playbook() + "\n\n" + (deckKnowledge ?? "") } },
                    new Dictionary<string, object> { { "role", "user" }, { "content", user + "\n/no_think" } }
                };

                byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(payload));
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                req.ContentLength = body.Length;
                string apiKey = CfgStr("api_key", "");
                if (apiKey.Length > 0) req.Headers["Authorization"] = "Bearer " + apiKey;
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    Dictionary<string, object> obj = Json.Deserialize<Dictionary<string, object>>(text);
                    IList choices = obj["choices"] as IList;
                    if (choices == null || choices.Count == 0) return null;
                    Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
                    if (first == null) return null;
                    Dictionary<string, object> message = first["message"] as Dictionary<string, object>;
                    if (message == null) return null;
                    string content = message.ContainsKey("content") ? message["content"] as string : null;
                    if (string.IsNullOrEmpty(content) && message.ContainsKey("reasoning_content"))
                        content = message["reasoning_content"] as string;
                    return content;
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("llm.log", DateTime.Now.ToString("HH:mm:ss") + " ask-error " + ex.Message + "\r\n"); } catch { }
                return null;
            }
        }
    }
}
