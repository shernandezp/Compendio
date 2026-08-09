# Configuring the AI assistant

**Administration → AI assistant.** Entirely optional. With nothing configured, no AI control
appears anywhere in the product — not greyed out, absent.

## Connecting a provider

One OpenAI-compatible endpoint covers Ollama, Groq, OpenAI, Azure OpenAI, LM Studio and vLLM.

- **Base URL** — the chat-completions base, without the trailing path. For Ollama on the same
  machine: `http://localhost:11434/v1`
- **Model** — a standard instruct model, for example `llama-3.3-70b-versatile`. Reasoning models
  (gpt-oss, DeepSeek-R1, the o-series) are slower and costlier here and are not recommended: this
  workload is summarizing and rewriting, not solving puzzles.
- **API key** — optional. Ollama and LM Studio do not need one. Once stored, leave the field blank
  to keep it or type a new one to replace it.

**Test connection** asks the model to answer and shows you what came back. Do this before telling
anyone the feature exists.

## The privacy decision

The panel states it plainly: page content is sent to the endpoint you configure whenever somebody
uses an AI action. **A model running on your own server keeps it on that machine.** That is the
whole reason the endpoint is configurable rather than fixed.

Pages inside encrypted folders are excluded unless you explicitly opt that folder in.

## Usage limits

A hosted endpoint charges per request, so cap what the wiki can spend in a rolling 24 hours:

- **Requests per person per day** — be generous. An editor working through a dozen pages spends
  about forty. Too tight and people stop trusting the feature.
- **Requests for everyone per day** — a second ceiling across the instance. Leave at 0 (no limit)
  unless the endpoint is metered.

A request that fails at the provider still counts, because by then it has already cost money.

The panel shows usage over the last 24 hours and who is spending the most — useful for spotting a
runaway integration rather than for performance management.

## Where AI may be used

- **Allowed spaces** — top-level folders the assistant may read. Empty means all of them. Use this
  to keep an entire area out of the assistant's reach without encrypting it.
- **Features** — turn individual features off: improve writing, draft from notes, summarize,
  translate, ask the wiki, freshness hints. An unchecked feature **disappears from the product**
  rather than failing when somebody uses it.

## Turning it off

**Turn AI off** removes every AI control everywhere, immediately. Nothing is sent anywhere. The
configuration is kept, so turning it back on does not mean re-entering the endpoint.
