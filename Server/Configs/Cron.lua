

function Run()
	Log.Info("Executed.");
end;

Actions["err"] = {
	Method = function () : table
		error("Error öß");
		return {};
	end
}