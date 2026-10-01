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
