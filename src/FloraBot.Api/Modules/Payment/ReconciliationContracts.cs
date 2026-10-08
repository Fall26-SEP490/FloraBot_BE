namespace FloraBot.Api.Modules.Payment;

// Names follow the source flow columns; the endpoint keeps the standard result envelope.
public sealed record GatewayReconciliationRow(string loai, string gateway_txn_id, long? he_thong, long? sao_ke, string ghi_chu);
public sealed record GatewayReconciliationResult(GatewayReconciliationRow[] Result);
public sealed record DailyReconciliationRow(string muc, long chung_tu, long so_cai, long lech, string ghi_chu);
public sealed record DailyReconciliationResult(DailyReconciliationRow[] Result);
