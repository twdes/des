
function DebugEnv.InitSession(session)

	-- create initial context
	--SetScope();

	session.BeginTransaction = function (userName)
		-- rollback current context
		EndScope(false, false);
		-- create the new context
		SetUser(userName);
	end;

	session.Commit = function ()
		EndScope(true, true);
	end;

	session.Rollback = function ()
		EndScope(false, true);
	end;
end;
