# LLM 决策是怎么接进去的

## 数据流

```
YGOPro 协议事件
   ↓
GameAI 的回调（OnSelectCard / OnSelectChain / OnSelectYesNo / OnSelectPlace …）
   ↓
LLMBrain：把局面整理成中文 → 拼提示词 → POST /chat/completions
   ↓
模型回一个数字或「是/否」
   ↓
LLMBrain 解析成 WindBot 需要的返回值
```

## 模型每回合看到什么

```
· 双方 LP、手牌数、卡组/额外剩余
· 自己场上每张卡（攻守、表示形式、可否发动）
· 对方场上已知的卡
· 当前回合数、阶段、当前连锁（谁发动了什么、取什么对象）
· 可选动作列表，带编号
· playbook.md（通用原则）
· Decks/<当前卡组>.md（这套牌的具体知识）
```

提示词要求模型**只输出编号**，解析失败时退回安全选项（不发动 / 结束回合），避免卡死。

## 回调对应

| WindBot 回调 | 模型回答 |
|---|---|
| `OnSelectCard` | 选第几张 |
| `OnSelectChain` | 连锁哪张，或「不连锁」 |
| `OnSelectYesNo` | 是 / 否 |
| `OnSelectPlace` | 放哪个区域（编号） |
| `OnSelectPosition` | 攻击 / 守备 |
| `OnSelectSum` | 选哪些卡、合计值 |
| `OnSelectUnselectCard` | 选或取消 |

## 配置项（App.config）

| key | 说明 |
|---|---|
| `endpoint` | 完整 URL，例如 `http://127.0.0.1:11450/v1/chat/completions` |
| `model` | 传给接口的 model 名 |
| `api_key` | 云端接口用；本地留空 |
| `UsePreErrataEffects` | 原版选项，保持 false |

## 卡组知识怎么写

`Decks/Yingcai.md` 是一个示例，结构：

```
## 终端
- 卡名：怎么出、必须放哪个区域、有什么限制

## 关键卡
- 昵称 = 正式卡名（模型可能只记得昵称）

## 常见操作
- 先做什么、后做什么、什么时候不要做
```

经验：**卡名写全名，别只写简称**；模型对全名的识别明显更稳。

## 延迟与成本

```
本地 30B（llama.cpp）：每手 1-3 秒
本地 7B：更快但更容易看错卡
云端：取决于网络，通常 1 秒内
```

每回合调用次数不固定（召唤、发动、连锁、选卡各一次），一局大约几十次。

## 调试

```
同目录 llm.log 会记录每次提问的事件摘要和模型原始回答
出牌不对时先看这里：是模型答错，还是解析错
```
