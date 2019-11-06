# Flashii Backup Manager

This is the program that runs every day at 12:00 AM UTC and backs up the non-volatile user data from [Flashii](https://flashii.net).
Provided for transparency.

## Grant line for MySQL backup user

```
GRANT SELECT, LOCK TABLES, SHOW VIEW, SHOW DATABASES, EVENT, TRIGGER, EXECUTE  ON *.* TO 'user'@'localhost';
```
