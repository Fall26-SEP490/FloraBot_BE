import { readFileSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
const migrationDir = new URL('../db/migrations/', import.meta.url);
const source = readFileSync(new URL('../db/FloraBot_DB_v3/03_flows.sql', import.meta.url), 'utf8') + '\n' +
  readdirSync(migrationDir).filter(name => name.endsWith('.sql')).sort().map(name => readFileSync(new URL(name, migrationDir), 'utf8')).join('\n');
const groups = {
  Identity: { seller: ['set_seller_bank', 'set_seller_tone', 'subscribe'], admin: ['approve_seller', 'set_seller_status'], customer: ['forget_customer'] },
  Catalog: { seller: ['set_product_status', 'update_product_price', 'add_product_photo', 'restock_accessory'] },
  KioskOps: { seller: ['release_slot', 'stock_bouquet', 'return_to_seller', 'report_device_fault'], admin: ['assign_slot', 'admin_dispose_slot', 'resolve_device_fault', 'set_kiosk_status', 'admin_close_door', 'set_cfg'], kiosk: ['kiosk_heartbeat', 'device_event'] },
  Ordering: { shopping: ['kiosk_checkout', 'request_pickup'], receipt: ['open_dispute', 'submit_refund_info'], admin: ['resolve_dispute'] },
  Payment: { sellerOnly: ['request_withdrawal'], admin: ['approve_withdrawal', 'pay_withdrawal', 'reject_withdrawal', 'admin_refund', 'create_refund', 'approve_refund', 'confirm_refund', 'reconcile_gateway', 'reconcile_daily'] },
  Ai: { shopping: ['ai_suggest'] },
  Notify: { admin: ['attach'] },
};
const actors = new Set(['p_user', 'p_admin', 'p_actor', 'p_staff', 'p_reporter', 'p_approver']);
const types = { uuid: 'Guid', text: 'string', int: 'int', bigint: 'long', numeric: 'decimal', boolean: 'bool', date: 'DateOnly', jsonb: 'JsonElement', 'uuid[]': 'Guid[]' };
const signatures = new Map([...source.matchAll(/CREATE OR REPLACE FUNCTION flow\.(\w+)\(([\s\S]*?)\)\s*RETURNS\s+(\w+)/g)].map(m => [m[1], { args: m[2].replace(/\s+/g, ' '), returns: m[3] }]));
const catalog = [];
for (const [module, roles] of Object.entries(groups)) {
  let code = 'using System.Text.Json;\nusing FloraBot.Api.Infrastructure;\n\nnamespace FloraBot.Api.Modules.' + module + ';\n\n';
  let routes = '';
  for (const [scope, names] of Object.entries(roles)) for (const name of names) {
    const params = signatures.get(name).args.split(/,\s*(?=p_)/).map(s => {
      const m = s.trim().match(/^(p_\w+)\s+(\w+(?:\[\])?)(?:\s+DEFAULT\s+(.+))?$/i);
      if (!m) throw new Error(name + ': ' + s);
      return { name: m[1], type: m[2], optional: m[3] !== undefined };
    });
    const injected = p => p.name === 'p_now' || actors.has(p.name) || (p.name === 'p_seller' && ['seller','sellerOnly'].includes(scope)) || (p.name === 'p_kiosk' && ['shopping','kiosk'].includes(scope)) || (p.name === 'p_customer' && ['shopping', 'customer'].includes(scope));
    const exposed = params.filter(p => !injected(p));
    const typeName = name.split('_').map(s => s[0].toUpperCase() + s.slice(1)).join('') + 'Request';
    code += `public sealed record ${typeName}(\n${exposed.map(p => `    ${types[p.type]}${p.optional || p.name === 'p_statement' ? '?' : ''} ${p.name}${p.optional ? ' = null' : ''}`).join(',\n')});\n\n`;
    const path = ['seller','sellerOnly'].includes(scope) ? '/sellers/{sellerId:guid}' : ['shopping','kiosk','customer'].includes(scope) ? '/kiosks/{kioskId:guid}' : scope === 'receipt' ? '/receipts' : '/admin';
    const policy = { seller:'Merchant', sellerOnly:'Seller', admin:'Admin', shopping:'Shopping', kiosk:'Kiosk', customer:'Customer', receipt:null }[scope];
    let authorization = ['seller', 'sellerOnly'].includes(scope) ? `.RequireAuthorization("${policy}", "SameSeller")` : policy ? `.RequireAuthorization("${policy}")` : '.AllowAnonymous().RequireRateLimiting("receipt")';
    if (name === 'ai_suggest') authorization += '.RequireRateLimiting("advisor")';
    const responseType = { reconcile_gateway: 'GatewayReconciliationResult', reconcile_daily: 'DailyReconciliationResult' }[name] || 'FlowResult';
    routes += `        app.MapPost("/api${path}/flows/${name}", async (${typeName} input, HttpContext http, FlowExecutor executor, ${name === "ai_suggest" ? "AiAdvisor advisor, " : ""}CancellationToken ct) =>\n            ${name === "ai_suggest" ? "await advisor.SuggestAsync(input, http, executor, ct)" : `await executor.ExecuteAsync("${name}", "${scope}", JsonSerializer.SerializeToElement(input), http, ct)`})\n            .WithName("${name}").WithTags("${module}").Produces<${responseType}>()${authorization};\n`;
    const setReturning = ['TABLE', 'SETOF'].includes(signatures.get(name).returns);
    catalog.push({ name, module, scope, params, setReturning });
  }
  code += `public static class ${module}Endpoints\n{\n    public static void Map${module}(this WebApplication app)\n    {\n${routes}    }\n}\n`;
  const dir = new URL('../src/FloraBot.Api/Modules/' + module + '/', import.meta.url);
  mkdirSync(dir, { recursive: true });
  writeFileSync(new URL('Endpoints.g.cs', dir), '// Generated from the supplied SQL by scripts/generate-flows.mjs.\n#nullable enable\n' + code);
}
writeFileSync(new URL('../src/FloraBot.Api/Infrastructure/flows.json', import.meta.url), JSON.stringify(catalog, null, 2) + '\n');
console.log(`Generated ${catalog.length} endpoints across ${Object.keys(groups).length} modules.`);
