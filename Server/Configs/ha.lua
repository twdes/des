s = [[Hello World!!!]]
for i = 1, s.Length do
  console.ForegroundColor = i + 1;
  console.Write(s[i - 1]);
end;