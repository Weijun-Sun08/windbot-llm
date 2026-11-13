using System;
using YGOSharp.OCGWrapper.Enums;
using WindBot;
using WindBot.Game;
using WindBot.Game.AI;

namespace WindBot.Game.AI.Executors
{
    // 盈彩月夜超量卡组：只注册卡组名，决策交给 LLM（LLMBrain）
    [Deck("Yingcai", "AI_Yingcai")]
    public class YingcaiExecutor : DefaultExecutor
    {
        public YingcaiExecutor(GameAI ai, Duel duel)
            : base(ai, duel)
        {
        }

        // 「盈彩月夜之天 西艾萝-一掷乾坤」（升月大姐）必须放在额外怪兽区才有效果。
        // 放置由本回调决定，默认实现返回 0（随便选），所以这里显式挑额外区（2026-11-13）。
        private const int YicaiBoss = 73090586;
        private const int ExtraMonsterZones = 0x20 | 0x40;

        public override int OnSelectPlace(int cardId, int player, CardLocation location, int available)
        {
            if (cardId == YicaiBoss)
            {
                int mask = available & ExtraMonsterZones;
                if (mask != 0) return mask & -mask;   // 取最低位的额外区
            }
            return base.OnSelectPlace(cardId, player, location, available);
        }
    }
}
