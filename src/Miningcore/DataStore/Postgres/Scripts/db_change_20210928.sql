ALTER TABLE shares 
ADD PRIMARY KEY IF NOT EXISTS (poolid, miner, accepted);