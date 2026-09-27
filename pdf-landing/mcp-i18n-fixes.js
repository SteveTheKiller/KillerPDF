Object.keys(I18N).forEach(function (locale) {
  var strings = I18N[locale];
  if (!strings || !strings.mcp_privacy_1) return;
  strings.mcp_privacy_1 = strings.mcp_privacy_1
    .replace(/[^.!?。！？]*Cloudflare[^.!?。！？]*[.!?。！？]?\s*/gu, " ")
    .trim();
});
