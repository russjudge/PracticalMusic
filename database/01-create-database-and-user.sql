-- =====================================================================================
-- Practical Music website - step 1: database and database user
--
-- Run as a MariaDB administrator (root), once per website installation:
--
--     sudo mariadb < 01-create-database-and-user.sql
--
-- BEFORE RUNNING: replace  CHANGE_ME_TO_A_LONG_RANDOM_PASSWORD  below with a strong password
-- (for example the output of:  openssl rand -base64 30 ). Put the same password into the
-- website's connection string (/etc/practicalmusic/appsettings.json, "ConnectionStrings:Default").
--
-- Every website installation has its own database: a second host simply runs these scripts
-- against its own MariaDB server. Nothing is shared between installations.
--
-- Works with MariaDB 10.6 or newer (Debian 13 ships MariaDB 11.8) and MySQL 8.
-- =====================================================================================

CREATE DATABASE IF NOT EXISTS practicalmusic
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

-- The website connects from the same machine, so the account is limited to localhost.
-- If MariaDB runs on a different machine, replace 'localhost' with the web server's address
-- (in all three statements below) and make sure MariaDB listens on the network (bind-address).
CREATE USER IF NOT EXISTS 'practicalmusic'@'localhost'
    IDENTIFIED BY 'CHANGE_ME_TO_A_LONG_RANDOM_PASSWORD';

-- If the user already existed (re-running the script), make sure the password is the new one.
ALTER USER 'practicalmusic'@'localhost'
    IDENTIFIED BY 'CHANGE_ME_TO_A_LONG_RANDOM_PASSWORD';

-- Least privilege: the website only reads and writes rows. It never changes the schema;
-- schema changes are applied by an administrator with the scripts in this folder.
GRANT SELECT, INSERT, UPDATE, DELETE
    ON practicalmusic.*
    TO 'practicalmusic'@'localhost';

FLUSH PRIVILEGES;
