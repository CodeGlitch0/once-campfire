-- Campfire SQLite schema
-- Source of truth: Rails db/schema.rb ActiveRecord::Schema[8.2] version 2025_12_12_154340
-- Types: integer primary keys; datetime and json stored as text; bigint stored as integer.
-- Only tables, columns, indexes, and foreign keys declared in schema.rb.
-- Applicable on a fresh SQLite database. Enable foreign keys for this connection.

PRAGMA foreign_keys = ON;

CREATE TABLE "accounts" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "custom_styles" TEXT,
  "join_code" TEXT NOT NULL,
  "name" TEXT NOT NULL,
  "settings" TEXT,
  "singleton_guard" INTEGER NOT NULL DEFAULT 0,
  "updated_at" TEXT NOT NULL
);

CREATE TABLE "action_text_rich_texts" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "body" TEXT,
  "created_at" TEXT NOT NULL,
  "name" TEXT NOT NULL,
  "record_id" INTEGER NOT NULL,
  "record_type" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL
);

CREATE TABLE "active_storage_blobs" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "byte_size" INTEGER NOT NULL,
  "checksum" TEXT,
  "content_type" TEXT,
  "created_at" TEXT NOT NULL,
  "filename" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "metadata" TEXT,
  "service_name" TEXT NOT NULL
);

CREATE TABLE "active_storage_attachments" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "blob_id" INTEGER NOT NULL,
  "created_at" TEXT NOT NULL,
  "name" TEXT NOT NULL,
  "record_id" INTEGER NOT NULL,
  "record_type" TEXT NOT NULL,
  FOREIGN KEY ("blob_id") REFERENCES "active_storage_blobs" ("id")
);

CREATE TABLE "active_storage_variant_records" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "blob_id" INTEGER NOT NULL,
  "variation_digest" TEXT NOT NULL,
  FOREIGN KEY ("blob_id") REFERENCES "active_storage_blobs" ("id")
);

CREATE TABLE "users" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "bio" TEXT,
  "bot_token" TEXT,
  "created_at" TEXT NOT NULL,
  "email_address" TEXT,
  "name" TEXT NOT NULL,
  "password_digest" TEXT,
  "role" INTEGER NOT NULL DEFAULT 0,
  "status" INTEGER NOT NULL DEFAULT 0,
  "updated_at" TEXT NOT NULL
);

CREATE TABLE "rooms" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "creator_id" INTEGER NOT NULL,
  "name" TEXT,
  "type" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL
);

CREATE TABLE "messages" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "client_message_id" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  "creator_id" INTEGER NOT NULL,
  "room_id" INTEGER NOT NULL,
  "updated_at" TEXT NOT NULL,
  FOREIGN KEY ("room_id") REFERENCES "rooms" ("id"),
  FOREIGN KEY ("creator_id") REFERENCES "users" ("id")
);

CREATE TABLE "bans" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "ip_address" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "user_id" INTEGER NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "users" ("id")
);

CREATE TABLE "boosts" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "booster_id" INTEGER NOT NULL,
  "content" VARCHAR(16) NOT NULL,
  "created_at" TEXT NOT NULL,
  "message_id" INTEGER NOT NULL,
  "updated_at" TEXT NOT NULL,
  FOREIGN KEY ("message_id") REFERENCES "messages" ("id")
);

CREATE TABLE "memberships" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "connected_at" TEXT,
  "connections" INTEGER NOT NULL DEFAULT 0,
  "created_at" TEXT NOT NULL,
  "involvement" TEXT DEFAULT 'mentions',
  "room_id" INTEGER NOT NULL,
  "unread_at" TEXT,
  "updated_at" TEXT NOT NULL,
  "user_id" INTEGER NOT NULL
);

CREATE TABLE "push_subscriptions" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "auth_key" TEXT,
  "created_at" TEXT NOT NULL,
  "endpoint" TEXT,
  "p256dh_key" TEXT,
  "updated_at" TEXT NOT NULL,
  "user_agent" TEXT,
  "user_id" INTEGER NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "users" ("id")
);

CREATE TABLE "searches" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "query" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "user_id" INTEGER NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "users" ("id")
);

CREATE TABLE "sessions" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "ip_address" TEXT,
  "last_active_at" TEXT NOT NULL,
  "token" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "user_agent" TEXT,
  "user_id" INTEGER NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "users" ("id")
);

CREATE TABLE "webhooks" (
  "id" INTEGER PRIMARY KEY NOT NULL,
  "created_at" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "url" TEXT,
  "user_id" INTEGER NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "users" ("id")
);

-- Virtual table from schema.rb: create_virtual_table "message_search_index", "fts5", ["body", "tokenize=porter"]
CREATE VIRTUAL TABLE "message_search_index" USING fts5(body, tokenize=porter);

CREATE UNIQUE INDEX "index_accounts_on_singleton_guard" ON "accounts" ("singleton_guard");

CREATE UNIQUE INDEX "index_action_text_rich_texts_uniqueness" ON "action_text_rich_texts" ("record_type", "record_id", "name");

CREATE INDEX "index_active_storage_attachments_on_blob_id" ON "active_storage_attachments" ("blob_id");
CREATE UNIQUE INDEX "index_active_storage_attachments_uniqueness" ON "active_storage_attachments" ("record_type", "record_id", "name", "blob_id");

CREATE UNIQUE INDEX "index_active_storage_blobs_on_key" ON "active_storage_blobs" ("key");

CREATE UNIQUE INDEX "index_active_storage_variant_records_uniqueness" ON "active_storage_variant_records" ("blob_id", "variation_digest");

CREATE INDEX "index_bans_on_ip_address" ON "bans" ("ip_address");
CREATE INDEX "index_bans_on_user_id" ON "bans" ("user_id");

CREATE INDEX "index_boosts_on_booster_id" ON "boosts" ("booster_id");
CREATE INDEX "index_boosts_on_message_id" ON "boosts" ("message_id");

CREATE INDEX "index_memberships_on_room_id_and_created_at" ON "memberships" ("room_id", "created_at");
CREATE UNIQUE INDEX "index_memberships_on_room_id_and_user_id" ON "memberships" ("room_id", "user_id");
CREATE INDEX "index_memberships_on_room_id" ON "memberships" ("room_id");
CREATE INDEX "index_memberships_on_user_id" ON "memberships" ("user_id");

CREATE INDEX "index_messages_on_creator_id" ON "messages" ("creator_id");
CREATE INDEX "index_messages_on_room_id" ON "messages" ("room_id");

CREATE INDEX "idx_on_endpoint_p256dh_key_auth_key_7553014576" ON "push_subscriptions" ("endpoint", "p256dh_key", "auth_key");
CREATE INDEX "index_push_subscriptions_on_user_id" ON "push_subscriptions" ("user_id");

CREATE INDEX "index_searches_on_user_id" ON "searches" ("user_id");

CREATE UNIQUE INDEX "index_sessions_on_token" ON "sessions" ("token");
CREATE INDEX "index_sessions_on_user_id" ON "sessions" ("user_id");

CREATE UNIQUE INDEX "index_users_on_bot_token" ON "users" ("bot_token");
CREATE UNIQUE INDEX "index_users_on_email_address" ON "users" ("email_address");

CREATE INDEX "index_webhooks_on_user_id" ON "webhooks" ("user_id");
