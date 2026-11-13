# WindBot + LLM

把 [WindBot](https://github.com/IceYGO/windbot)（MIT）的**手工规则 AI** 换成**大语言模型决策**：每回合把局面整理成一小段中文，问一个 OpenAI 兼容接口，拿回「召唤谁 / 发不发效果 / 选哪张 / 打不打」，再翻译回 WindBot 的返回值。

原版 WindBot 靠固定出牌脚本；这个版本让模型看局面自己决定，适合做「LLM 打牌」实验，也适合给自己喜欢的卡组接一个会说话的 AI。

## 和原版的区别

```
新增  Game/LLMBrain.cs          提示词、局面摘要、HTTP 调用、答案解析
改动  Game/GameAI.cs            召唤/发动/选择/连锁/是否/放置 等回调转给 LLMBrain
新增  Game/AI/Executors/*.cs    按卡组注册执行器（示例：Yingcai）
新增  playbook.md               通用打牌原则（注入系统提示）
新增  Decks/*.md                单卡组知识（卡名、连招、注意事项）
配置  App.config                接口地址、模型名、API Key
```

## 快速开始

### 1. 准备一个 OpenAI 兼容接口

```
llama.cpp   llama-server -m model.gguf --port 11450 --jinja
Ollama      ollama serve（端点 http://127.0.0.1:11434/v1）
vLLM        自带 OpenAI 兼容
云端        https://api.deepseek.com 等
```

### 2. 配置接口

编辑 `App.config`：

```xml
<appSettings>
  <add key="UsePreErrataEffects" value="false"/>
  <add key="endpoint" value="http://127.0.0.1:11450/v1/chat/completions"/>
  <add key="model" value="local"/>
  <add key="api_key" value=""/>
</appSettings>
```

### 3. 编译

```
dotnet build WindBot.csproj -c Release
```

需要 .NET Framework 4.8（Windows）或 Mono。

### 4. 放 cards.cdb

原版仓库不提供卡库；从 YGOPro 客户端目录复制 `cards.cdb`，放到 `WindBot.exe` 旁边。

### 5. 连服务器

命令行与原版一致，例如：

```
WindBot.exe "127.0.0.1" 7911 "AI#Yingcai" "Yingcai" 0 0 0 0
```

或按原版 `BotWrapper/bot.conf` / `[Deck]` 声明的方式接入 srvpro。

## 卡组知识

`playbook.md` 是通用原则；`Decks/<卡组名>.md` 是这套牌的具体知识（终端怎么出、哪张必须放额外区、什么时候交手坑）。

模型每回合都读这两份文件，所以**改打法不用重新训练，改文字就行**。

加自己的卡组：

```
1. 复制 Decks/Yingcai.md 改名
2. 写卡名、关键连招、限制
3. 在 Game/AI/Executors 加一个执行器继承 DefaultExecutor，
   标注 [Deck("YourDeck", "AI_YourDeck")]
4. 放一份 AI_YourDeck.ydk 到 Decks/
```

## 已知限制

```
· 决策延迟取决模型：本地 30B 约 1-3 秒一手，云端快一些
· 模型可能记错卡名/效果，所以需要 Decks/*.md 兜底
· 只测过 SRVPro + YGOPro 协议，其他分支未验证
· 没有回放学习，一切靠提示词
```

## 许可与来源

```
基于 WindBot（Copyright (c) 2015-2017 IceYGO，MIT）。
新增的 LLM 部分同样以 MIT 发布，详见 LICENSE 与 NOTICE。
```

## 相关项目

```
群聊「什么时候该接话」判断器：
https://github.com/Weijun-Sun08/qq-addressedness-gate
```
