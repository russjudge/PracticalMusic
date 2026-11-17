-- =====================================================================================
-- Practical Music website - optional: make an existing account the website administrator
--
-- The usual way to create the administrator is the website's command line (see
-- docs/INSTALL-Server-Debian.md):
--
--     sudo -u practicalmusic /opt/practicalmusic/PracticalMusic.Web create-admin --email you@example.com --name "Your Name"
--
-- Alternatively, sign up on the website like any user, then run this script (after
-- replacing the address) as a MariaDB administrator:
--
--     sudo mariadb practicalmusic < 03-promote-site-admin.sql
--
-- The administrator may do anything: create organizations, manage every organization and user.
-- =====================================================================================

USE practicalmusic;

UPDATE users
   SET is_site_admin     = 1,
       is_disabled       = 0,
       email_verified_at = COALESCE(email_verified_at, UTC_TIMESTAMP(3)),
       updated_at        = UTC_TIMESTAMP(3)
 WHERE email = LOWER('CHANGE_ME@example.com');

SELECT id, email, full_name, is_site_admin, email_verified_at FROM users WHERE is_site_admin = 1;
