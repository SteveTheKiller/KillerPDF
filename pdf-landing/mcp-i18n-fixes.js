Object.assign(I18N.vi, {
  mcp_step_2: "<strong>Cho phép trình cài đặt kết nối trợ lý AI của bạn.</strong> Trình cài đặt cài đặt môi trường chạy dùng chung và đăng ký KillerMCP với Codex, Claude Code, Claude Desktop, Cursor, GitHub Copilot, Gemini CLI và Windsurf khi tìm thấy.",
  mcp_step_3: "<strong>Mở một cuộc trò chuyện mới với trợ lý AI và đặt câu hỏi.</strong> KillerMCP phát hiện KillerPDF trên máy tính của bạn và cung cấp các công cụ PDF qua cùng một kết nối.",
  mcp_examples_intro: "<code>killer</code> là dạng ngắn nhất. Trợ lý AI có thể nhận biết KillerPDF khi yêu cầu và các tệp đính kèm rõ ràng liên quan đến PDF. Chỉ dùng <code>killerpdf</code> khi bạn muốn chỉ định ứng dụng.",
  mcp_connection_body: "Chỉ cần cài đặt KillerMCP một lần. Phần mềm phát hiện các ứng dụng Killer được hỗ trợ trên máy tính và thêm công cụ của chúng vào một kết nối với trợ lý AI. Khi có thêm tích hợp ứng dụng, cùng một môi trường chạy KillerMCP có thể cung cấp chúng mà không cần trình cài đặt riêng cho từng ứng dụng."
});

Object.keys(I18N).forEach(function (locale) {
  var strings = I18N[locale];
  if (!strings || !strings.mcp_privacy_1) return;
  strings.mcp_privacy_1 = strings.mcp_privacy_1
    .replace(/[^.!?。！？]*Cloudflare[^.!?。！？]*[.!?。！？]?\s*/gu, " ")
    .trim();
});
if (I18N.uk) Object.assign(I18N.uk, {
 "mcp_step_2": "<strong>Дозвольте інсталятору підключити вашого ШІ-асистента.</strong> Інсталятор установлює спільне середовище виконання та реєструє KillerMCP у Codex, Claude Code, Claude Desktop, Cursor, GitHub Copilot, Gemini CLI і Windsurf, коли знаходить їх.",
 "mcp_step_3": "<strong>Відкрийте новий чат із ШІ-асистентом і поставте запитання.</strong> KillerMCP виявляє KillerPDF на вашому комп'ютері та робить його інструменти PDF доступними через те саме підключення.",
 "mcp_examples_intro": "<code>killer</code> - це найкоротша форма. ШІ-асистент може здогадатися про KillerPDF, коли запит і вкладення явно стосуються PDF. Використовуйте <code>killerpdf</code> лише тоді, коли хочете явно вказати застосунок.",
 "mcp_connection_body": "Встановіть KillerMCP один раз. Він виявляє підтримувані застосунки Killer на вашому комп'ютері та додає їхні інструменти до одного підключення ШІ-асистента. Коли виходять нові інтеграції застосунків, те саме середовище виконання KillerMCP може надавати їх без окремого інсталятора для кожного застосунку."
});
if (I18N.nb) Object.assign(I18N.nb, {
 "mcp_step_2": "<strong>La oppsettet koble til agenten din.</strong> Det installerer den delte kjøretiden og registrerer KillerMCP i Codex, Claude Code, Claude Desktop, Cursor, GitHub Copilot, Gemini CLI og Windsurf når de blir funnet.",
 "mcp_step_3": "<strong>Åpne en ny agentchat og spør.</strong> KillerMCP oppdager KillerPDF på datamaskinen din og gjør PDF-verktøyene tilgjengelige gjennom den samme tilkoblingen.",
 "mcp_examples_intro": "<code>killer</code> er den korteste formen. Agenten din kan utlede KillerPDF når forespørselen og de vedlagte filene tydelig gjelder PDF-er. Bruk <code>killerpdf</code> bare når du vil gjøre programmet eksplisitt.",
 "mcp_connection_body": "Installer KillerMCP én gang. Det oppdager støttede Killer-programmer på datamaskinen din og legger verktøyene deres til i én agenttilkobling. Etter hvert som flere programintegrasjoner utgis, kan den samme KillerMCP-kjøretiden levere dem uten et eget installasjonsprogram for hvert program."
});
if (I18N.pt) Object.assign(I18N.pt, {
 "mcp_step_2": "<strong>Deixe o instalador conectar seu agente.</strong> Ele instala o runtime compartilhado e registra o KillerMCP no Codex, no Claude Code, no Claude Desktop, no Cursor, no GitHub Copilot, no Gemini CLI e no Windsurf quando os encontra.",
 "mcp_step_3": "<strong>Abra um novo chat com o agente e pergunte.</strong> O KillerMCP detecta o KillerPDF no seu computador e disponibiliza as ferramentas de PDF dele pela mesma conexão.",
 "mcp_examples_intro": "<code>killer</code> é a forma mais curta. Seu agente pode inferir o KillerPDF quando o pedido e os arquivos anexados são claramente sobre PDFs. Use <code>killerpdf</code> somente quando quiser deixar o aplicativo explícito.",
 "mcp_connection_body": "Instale o KillerMCP uma única vez. Ele detecta os aplicativos Killer compatíveis no seu computador e adiciona as ferramentas deles a uma única conexão com o agente. À medida que mais integrações de aplicativos forem lançadas, o mesmo runtime do KillerMCP poderá oferecê-las sem um instalador separado para cada aplicativo."
});
