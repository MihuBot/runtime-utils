#!/bin/bash

# Clean up the runner work directory before each run
find /runner -mindepth 1 -delete 2>/dev/null || true

dotnet /app/Runner.dll "$@"
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo "Runner exited with code $exit_code, sleeping for 60 seconds..."
    sleep 60
fi

exit $exit_code
