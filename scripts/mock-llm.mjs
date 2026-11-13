// 开发用 mock LLM：OpenAI chat-completions 兼容 SSE 流（reasoning + content）。
// 用途：qaqh-cs-app 的端到端流式验收（无真实 API key 时的本地 provider）。
// 用法：node scripts/mock-llm.mjs [port]   默认 8318
import http from 'node:http';

const port = Number(process.argv[2] ?? 8318);

const REASONING = [
  '先拆解用户的问题：需要一段自我介绍。',
  '组织结构：身份 → 能力边界 → 可以帮什么忙。',
  '准备用 Markdown：一句加粗开场 + 列表。',
];

const ANSWER = [
  '你好！**我是 QAQ Harness 的编程助手**。\n\n',
  '我能帮你做这些事：\n\n',
  '- **读写代码**：在授权的工作区内检索、编辑、重构\n',
  '- **执行命令**：跑构建/测试，并把输出流式回显给你\n',
  '- **计划与审批**：先给计划，由你决定是否执行\n\n',
  '有什么想让我做的，直接说就行。',
];

function sseChunk(res, obj) {
  res.write(`data: ${JSON.stringify(obj)}\n\n`);
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/v1/models') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ object: 'list', data: [{ id: 'mock-1', object: 'model' }] }));
    return;
  }
  if (req.method !== 'POST' || !req.url.startsWith('/v1/chat/completions')) {
    res.writeHead(404).end();
    return;
  }
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    console.log(`[${new Date().toISOString()}] ${req.url} stream req`);
    res.writeHead(200, {
      'content-type': 'text/event-stream',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    });
    const id = `chatcmpl-mock-${Date.now()}`;
    let index = 0;
    const emit = (delta) =>
      sseChunk(res, {
        id,
        object: 'chat.completion.chunk',
        created: Math.floor(Date.now() / 1000),
        model: 'mock-1',
        choices: [{ index: 0, delta, finish_reason: null }],
      });

    const stream = () => {
      if (index < REASONING.length) {
        emit({ reasoning_content: REASONING[index] });
      } else if (index < REASONING.length + ANSWER.length) {
        emit({ content: ANSWER[index - REASONING.length] });
      } else {
        emit({});
        sseChunk(res, {
          id,
          object: 'chat.completion.chunk',
          created: Math.floor(Date.now() / 1000),
          model: 'mock-1',
          choices: [{ index: 0, delta: {}, finish_reason: 'stop' }],
          usage: { prompt_tokens: 10, completion_tokens: 40, total_tokens: 50 },
        });
        res.write('data: [DONE]\n\n');
        res.end();
        return;
      }
      index += 1;
      setTimeout(stream, 220);
    };
    stream();
  });
});

server.listen(port, '127.0.0.1', () => console.log(`mock-llm listening on http://127.0.0.1:${port}/v1`));
