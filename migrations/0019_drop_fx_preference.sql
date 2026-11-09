-- The "趣味动效" easter egg has been removed; drop its stored preference so
-- accounts do not keep a dead key around.
DELETE FROM user_preferences WHERE key = 'fx';
