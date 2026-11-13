using WindBot.Game.AI;

namespace WindBot.Game.AI.Executors
{
    // 星辰 + 半龙女仆 混轴：只用来注册卡组名，具体决策交给 LLM（LLMBrain）处理
    [Deck("DracotailMaid", "AI_DracotailMaid")]
    public class DracotailMaidExecutor : DefaultExecutor
    {
        public DracotailMaidExecutor(GameAI ai, Duel duel)
            : base(ai, duel)
        {
        }
    }
}
