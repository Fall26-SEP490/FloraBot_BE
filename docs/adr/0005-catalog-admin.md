# Catalog administrator authorization

The supplied D2 matrix permits ADMIN and the owning SELLER to change product price/status.
The original SQL restricts both commands to users with the product's seller_id, excluding admins.
Migration 002 replaces only those two functions, retaining their signatures and failure messages.
An active ADMIN or active owning SELLER may mutate the product; other roles are denied.
The API still injects the authenticated actor and checks seller ownership before SQL execution.
Price changes retain the original audit trigger; status changes now record an audit event too.
The source archive is unchanged and original regression verification remains independently runnable.
Integration tests cover admin success, authenticated audit attribution and foreign-seller rejection.
Apply the migration transactionally to existing databases; fresh initialization applies it automatically.
