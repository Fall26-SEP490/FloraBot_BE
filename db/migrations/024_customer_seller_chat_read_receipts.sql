BEGIN;

-- Initialize the cursor only when this feature is first installed. The runtime
-- migration runner replays every migration, so this guard preserves later unread rows.
DO $migration$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_attribute
    WHERE attrelid = 'ordering.customer_seller_chat_messages'::regclass
      AND attname = 'read_at'
      AND NOT attisdropped
  ) THEN
    ALTER TABLE ordering.customer_seller_chat_messages
      ADD COLUMN read_at timestamptz;

    -- Existing history predates read receipts; don't present all old messages as unread.
    UPDATE ordering.customer_seller_chat_messages
    SET read_at = created_at
    WHERE read_at IS NULL;
  END IF;
END
$migration$;

CREATE INDEX IF NOT EXISTS customer_seller_chat_messages_unread
  ON ordering.customer_seller_chat_messages(chat_id, sender_user_id)
  WHERE read_at IS NULL;

COMMIT;
