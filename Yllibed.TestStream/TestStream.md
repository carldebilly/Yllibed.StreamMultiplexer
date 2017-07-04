# TestStream Utility

## Features

* Ability to simulate high latency and high-jitter streams
* Ability to simulate transmit fragmentation
* Ability to limit bandwidth
* No need to create a `NetworkStream`: code won't need special priviledges to run
  (perfect for unit testing)
* Designed for tests with stream-specific assertion features:
  * Assert transfert size
  * Assert stream state
  * Assert sync/async operations

## Sample usage
Let's start with a sample code using the `TestStream`:

``` csharp
[TestMethod]
public async Task SampleTest()
{
	using(var sut = new TestStream())
	{
		sut
			.AllowOnlyAsyncOperations(); // sync operations will throw exceptions

		async Task WriteToStreamA(string txt)
		{
			var buffer = UTF8.GetBytes(txt);
			await sut.StreamA.WriteAsync(buffer, 0, buffer.Length);
		}

		async Task<string> ReadFromStreamB()
		{
			var buffer = new byte[2048];
			var readLength = await sut.StreamB.ReadAsync(buffer, 0, 2048);
			return UTF8.GetString(buffer, 0, readLength);
		}

		// Start write task without waiting for its completition.
		var taskWrite = WriteToStreamA("this is a test");
		
		var readText = await ReadFromStreamB();
		Assert.AreEquals("this is a test", readText);
	}
}
```
> Note: This code is using _MS-Tests_ framework, but should work with any testing framework.