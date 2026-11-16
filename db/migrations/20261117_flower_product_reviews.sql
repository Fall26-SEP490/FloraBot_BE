BEGIN;

CREATE TABLE catalog.flower_product_reviews (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id uuid NOT NULL REFERENCES catalog.flower_products(id),
    order_id uuid NOT NULL REFERENCES ordering.orders(id),
    customer_id uuid NOT NULL REFERENCES identity.users(id),
    stars smallint NOT NULL CHECK (stars BETWEEN 1 AND 5),
    comment text CHECK (char_length(comment) <= 1000),
    photo bytea CHECK (octet_length(photo) BETWEEN 1 AND 2097152),
    photo_type text CHECK (photo_type IN ('image/jpeg', 'image/png', 'image/webp')),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (product_id, customer_id),
    CHECK ((photo IS NULL) = (photo_type IS NULL))
);

CREATE INDEX flower_product_reviews_recent
    ON catalog.flower_product_reviews (product_id, created_at DESC, id DESC);

COMMIT;
