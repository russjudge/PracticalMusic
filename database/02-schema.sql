-- =====================================================================================
-- Practical Music website - step 2: tables (schema version 1)
--
-- Run as a MariaDB administrator after 01-create-database-and-user.sql:
--
--     sudo mariadb practicalmusic < 02-schema.sql
--
-- Safe to run again: existing tables are left alone. The website checks the schema version at
-- start-up and refuses to run against a database that is older than it expects.
--
-- All times are stored in UTC.
-- =====================================================================================

USE practicalmusic;

-- -------------------------------------------------------------------------------------
-- People. The e-mail address is the user name. password_hash is NULL for invited users
-- who have not chosen a password yet.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
    id                BIGINT          NOT NULL AUTO_INCREMENT,
    email             VARCHAR(254)    CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, -- stored lower-case; compared exactly
    password_hash     VARCHAR(255)    NULL,
    full_name         VARCHAR(120)    NOT NULL,
    phone             VARCHAR(40)     NULL,
    address           VARCHAR(500)    NULL,
    is_site_admin     TINYINT(1)      NOT NULL DEFAULT 0,    -- website administrator: may do anything
    is_disabled       TINYINT(1)      NOT NULL DEFAULT 0,
    email_verified_at DATETIME(3)     NULL,
    security_stamp    CHAR(32)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL, -- changes on password change: signs out old sessions
    failed_logins     INT             NOT NULL DEFAULT 0,
    locked_until      DATETIME(3)     NULL,
    created_at        DATETIME(3)     NOT NULL,
    updated_at        DATETIME(3)     NOT NULL,
    last_login_at     DATETIME(3)     NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_users_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Organizations (a church, a band, ...). Created by the website administrator only.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS organizations (
    id                  BIGINT          NOT NULL AUTO_INCREMENT,
    name                VARCHAR(120)    NOT NULL,
    kind                ENUM('church','band','choir','school','other') NOT NULL DEFAULT 'church',
    description         VARCHAR(1000)   NULL,
    contact_email       VARCHAR(254)    NULL,
    allow_join_requests TINYINT(1)      NOT NULL DEFAULT 1,  -- listed on the sign-up page
    is_active           TINYINT(1)      NOT NULL DEFAULT 1,
    created_at          DATETIME(3)     NOT NULL,
    created_by          BIGINT          NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_organizations_name (name),
    CONSTRAINT fk_organizations_created_by FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Who belongs to which organization. A user may belong to many organizations.
--   role   manager = maintains books/files and manages the organization's users
--          member  = reads and plays the books
--   status pending  = asked to join, waiting for a manager; active = has access;
--          rejected = a manager declined the request (the user may ask again);
--          invited  = a manager invited someone who already has an account; they must accept
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS memberships (
    user_id         BIGINT          NOT NULL,
    org_id          BIGINT          NOT NULL,
    role            ENUM('manager','member')            NOT NULL DEFAULT 'member',
    status          ENUM('pending','active','rejected','invited') NOT NULL DEFAULT 'pending',
    request_message VARCHAR(1000)   NULL,
    requested_at    DATETIME(3)     NOT NULL,
    decided_at      DATETIME(3)     NULL,
    decided_by      BIGINT          NULL,
    PRIMARY KEY (user_id, org_id),
    KEY ix_memberships_org (org_id, status),
    CONSTRAINT fk_memberships_user       FOREIGN KEY (user_id)    REFERENCES users (id)         ON DELETE CASCADE,
    CONSTRAINT fk_memberships_org        FOREIGN KEY (org_id)     REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT fk_memberships_decided_by FOREIGN KEY (decided_by) REFERENCES users (id)         ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Uploaded PDF and audio files. The bytes live on disk under <DataDir>/media/<sha256>,
-- shared between organizations when identical, but every organization has its own row,
-- so access is always checked per organization.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS media (
    id           BIGINT          NOT NULL AUTO_INCREMENT,
    org_id       BIGINT          NOT NULL,
    sha256       CHAR(64)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    kind         ENUM('pdf','audio') NOT NULL,
    file_name    VARCHAR(255)    NOT NULL,
    content_type VARCHAR(100)    NOT NULL,
    size_bytes   BIGINT          NOT NULL,
    page_count   INT             NULL,                       -- PDFs only
    created_at   DATETIME(3)     NOT NULL,
    created_by   BIGINT          NULL,
    last_used_at DATETIME(3)     NOT NULL,                   -- uploaded again / saved in a book; unused files are removed after a grace period
    PRIMARY KEY (id),
    UNIQUE KEY ux_media_org_sha (org_id, sha256),
    KEY ix_media_sha (sha256),
    KEY ix_media_last_used (last_used_at),
    CONSTRAINT fk_media_org        FOREIGN KEY (org_id)     REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT fk_media_created_by FOREIGN KEY (created_by) REFERENCES users (id)         ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Books (the same content as a .pmbook's book.json). Every save makes a new revision;
-- fingerprint identifies the content (see BookFingerprint in the app's code).
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS books (
    id          BIGINT          NOT NULL AUTO_INCREMENT,
    org_id      BIGINT          NOT NULL,
    book_uid    CHAR(32)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL, -- "id" inside book.json
    title       VARCHAR(200)    NOT NULL,
    revision    INT             NOT NULL DEFAULT 1,
    fingerprint CHAR(64)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    book_json   LONGTEXT        NOT NULL,
    song_count  INT             NOT NULL DEFAULT 0,
    media_bytes BIGINT          NOT NULL DEFAULT 0,
    created_at  DATETIME(3)     NOT NULL,
    created_by  BIGINT          NULL,
    updated_at  DATETIME(3)     NOT NULL,
    updated_by  BIGINT          NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_books_org_uid (org_id, book_uid),
    KEY ix_books_org_title (org_id, title),
    CONSTRAINT fk_books_org        FOREIGN KEY (org_id)     REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT fk_books_created_by FOREIGN KEY (created_by) REFERENCES users (id)         ON DELETE SET NULL,
    CONSTRAINT fk_books_updated_by FOREIGN KEY (updated_by) REFERENCES users (id)         ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Which media file each embedded entry of a book (book.json "entryName") refers to.
CREATE TABLE IF NOT EXISTS book_media (
    book_id    BIGINT          NOT NULL,
    entry_name VARCHAR(300)    CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, -- exact: "Canción.pdf" and "Cancion.pdf" differ
    media_id   BIGINT          NOT NULL,
    PRIMARY KEY (book_id, entry_name),
    KEY ix_book_media_media (media_id),
    CONSTRAINT fk_book_media_book  FOREIGN KEY (book_id)  REFERENCES books (id) ON DELETE CASCADE,
    -- a file a book uses can never be deleted underneath it
    CONSTRAINT fk_book_media_media FOREIGN KEY (media_id) REFERENCES media (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Sign-ins of the desktop and Android apps. Only a SHA-256 hash of the token is stored.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS api_tokens (
    id           BIGINT          NOT NULL AUTO_INCREMENT,
    user_id      BIGINT          NOT NULL,
    token_hash   CHAR(64)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_name  VARCHAR(120)    NOT NULL,
    platform     VARCHAR(40)     NULL,
    created_at   DATETIME(3)     NOT NULL,
    last_used_at DATETIME(3)     NULL,
    expires_at   DATETIME(3)     NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_api_tokens_hash (token_hash),
    KEY ix_api_tokens_user (user_id),
    CONSTRAINT fk_api_tokens_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- One-time links sent by e-mail (confirm address, reset password, invitation). Hashed.
CREATE TABLE IF NOT EXISTS user_tokens (
    id         BIGINT          NOT NULL AUTO_INCREMENT,
    user_id    BIGINT          NOT NULL,
    purpose    ENUM('verify_email','reset_password','invite') NOT NULL,
    token_hash CHAR(64)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    created_at DATETIME(3)     NOT NULL,
    expires_at DATETIME(3)     NOT NULL,
    used_at    DATETIME(3)     NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_user_tokens_hash (token_hash),
    KEY ix_user_tokens_user (user_id, purpose),
    CONSTRAINT fk_user_tokens_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- Outgoing e-mail. Messages are queued here and sent (with retries) by the website;
-- the administrator's "E-mail" page shows failures.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS email_outbox (
    id              BIGINT          NOT NULL AUTO_INCREMENT,
    to_address      VARCHAR(254)    NOT NULL,
    to_name         VARCHAR(120)    NULL,
    subject         VARCHAR(300)    NOT NULL,
    body_text       MEDIUMTEXT      NOT NULL,
    body_html       MEDIUMTEXT      NULL,
    status          ENUM('queued','sent','failed') NOT NULL DEFAULT 'queued',
    attempts        INT             NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3)     NOT NULL,
    last_error      VARCHAR(2000)   NULL,
    created_at      DATETIME(3)     NOT NULL,
    sent_at         DATETIME(3)     NULL,
    PRIMARY KEY (id),
    KEY ix_email_outbox_due (status, next_attempt_at),
    KEY ix_email_outbox_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
-- What happened, for the administrator and organization managers. No foreign keys:
-- the history stays when users or organizations are deleted.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_log (
    id      BIGINT          NOT NULL AUTO_INCREMENT,
    at      DATETIME(3)     NOT NULL,
    user_id BIGINT          NULL,
    org_id  BIGINT          NULL,
    action  VARCHAR(60)     NOT NULL,
    details VARCHAR(2000)   NULL,
    ip      VARCHAR(45)     NULL,
    PRIMARY KEY (id),
    KEY ix_audit_at (at),
    KEY ix_audit_org (org_id, at),
    KEY ix_audit_user (user_id, at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS schema_version (
    version     INT          NOT NULL,
    applied_at  DATETIME(3)  NOT NULL,
    description VARCHAR(200) NOT NULL,
    PRIMARY KEY (version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO schema_version (version, applied_at, description)
VALUES (1, UTC_TIMESTAMP(3), 'Initial schema: users, organizations, memberships, books, media, tokens, e-mail outbox, audit log');
