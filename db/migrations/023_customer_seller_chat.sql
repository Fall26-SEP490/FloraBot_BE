BEGIN;

CREATE TABLE IF NOT EXISTS ordering.customer_seller_chats (
  id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  customer_id uuid NOT NULL REFERENCES identity.users(id),
  seller_id uuid NOT NULL REFERENCES identity.sellers(id),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  last_message_at timestamptz,
  CONSTRAINT customer_seller_chats_pair_key UNIQUE (customer_id, seller_id)
);
CREATE INDEX IF NOT EXISTS customer_seller_chats_customer_recent
  ON ordering.customer_seller_chats(customer_id, last_message_at DESC NULLS LAST, id DESC);
CREATE INDEX IF NOT EXISTS customer_seller_chats_seller_recent
  ON ordering.customer_seller_chats(seller_id, last_message_at DESC NULLS LAST, id DESC);

CREATE TABLE IF NOT EXISTS ordering.customer_seller_chat_products (
  chat_id uuid NOT NULL REFERENCES ordering.customer_seller_chats(id) ON DELETE CASCADE,
  product_id uuid NOT NULL REFERENCES catalog.flower_products(id),
  product_name_snapshot text NOT NULL,
  first_opened_at timestamptz NOT NULL DEFAULT public.app_now(),
  PRIMARY KEY (chat_id, product_id)
);
CREATE INDEX IF NOT EXISTS customer_seller_chat_products_recent
  ON ordering.customer_seller_chat_products(chat_id, first_opened_at DESC);

CREATE TABLE IF NOT EXISTS ordering.customer_seller_chat_messages (
  id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  chat_id uuid NOT NULL REFERENCES ordering.customer_seller_chats(id),
  sender_user_id uuid NOT NULL REFERENCES identity.users(id),
  content text CHECK (content IS NULL OR (length(content) <= 2000 AND length(btrim(content)) > 0)),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  CONSTRAINT customer_seller_chat_messages_id_chat_key UNIQUE (id, chat_id)
);
CREATE INDEX IF NOT EXISTS customer_seller_chat_messages_history
  ON ordering.customer_seller_chat_messages(chat_id, created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS ordering.customer_seller_chat_images (
  id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  message_id uuid NOT NULL UNIQUE,
  chat_id uuid NOT NULL,
  file_url text NOT NULL UNIQUE,
  mime_type text NOT NULL CHECK (mime_type IN ('image/jpeg','image/png','image/webp')),
  size_bytes bigint NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 5242880),
  sha256 text NOT NULL,
  uploaded_by uuid NOT NULL REFERENCES identity.users(id),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  CONSTRAINT customer_seller_chat_images_message_chat_fk
    FOREIGN KEY (message_id, chat_id)
    REFERENCES ordering.customer_seller_chat_messages(id, chat_id)
    ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS customer_seller_chat_images_chat
  ON ordering.customer_seller_chat_images(chat_id, created_at DESC);

COMMIT;
